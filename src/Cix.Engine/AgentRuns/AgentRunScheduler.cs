using System.Globalization;
using System.Text.Json;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Tools;

namespace Cix.Engine.AgentRuns;

/// <summary>Where runs go: one queue per job. A fake in tests, Storage queues in Azure.</summary>
public interface IAgentRunQueue
{
    /// <summary>Queues the run for its job; the message expires after <paramref name="timeToLive"/>.</summary>
    Task SendAsync(AgentRun run, TimeSpan timeToLive, CancellationToken cancellationToken);
}

/// <summary>The saved <see cref="AgentRunState"/>: in memory by default, Cosmos DB with AddCixCosmos.</summary>
public interface IAgentRunStateStore
{
    Task<AgentRunState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AgentRunState state, CancellationToken cancellationToken);
}

public sealed class InMemoryAgentRunStateStore : IAgentRunStateStore
{
    private AgentRunState? _saved;

    public int Saves { get; private set; }

    public Task<AgentRunState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Volatile.Read(ref _saved));

    public Task SaveAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _saved, state);
        Saves++;
        return Task.CompletedTask;
    }
}

/// <summary>What the run rules look at.</summary>
public interface IAgentRunSignals
{
    AgentRunInputs Read();
}

/// <summary>The scanner's latest signals and the owner's waiting requests: what the shell loops read from <c>/signals</c>.</summary>
public sealed class EngineAgentRunSignals(MarketScanService scanner, ManualRequestService requests) : IAgentRunSignals
{
    public AgentRunInputs Read() => new(scanner.Latest is not null, scanner.SignalDriven, scanner.SignalKey, requests.AgentKey);
}

/// <summary>
/// Decides when the agents run and queues each run for its job (ADR-0013), with the rules the shell loops follow in Compose
/// (<see cref="AgentRunPlanner"/>). A job claims its run before it starts Claude and reports how it ended; the engine
/// counts the caps, refunds runs that never used Claude, and retries. Every step is journaled as <c>agent-run</c>.
/// </summary>
/// <remarks>
/// Only the engine holding the engine lease queues runs or answers jobs, so two engines during a deployment can't both
/// start a run. The state is saved before a run is queued: a crash after the send can't lose it and queue it again.
/// </remarks>
public sealed partial class AgentRunScheduler(
    AgentRunOptions options,
    IAgentRunSignals signals,
    IAgentRunStateStore store,
    IAgentRunQueue queue,
    IJournal journal,
    TimeProvider time,
    ILogger<AgentRunScheduler> logger,
    IEngineLease? lease = null) : BackgroundService
{
    public const string Kind = "agent-run";
    public const string Actor = "engine";

    /// <summary>A queue send gets this long; the run is then counted as not run and tried again by the rules.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentRunPlanner _planner = new(options);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentRunState? _state;
    private string? _saved;

    /// <summary>Whether this engine schedules runs now: only while it holds the engine lease.</summary>
    public bool IsActive => lease is not { IsHeld: false };

    /// <summary>The state as last saved, or null before it is loaded.</summary>
    public AgentRunState? State => Volatile.Read(ref _state);

    /// <summary>One look at the signals and requests: queues the runs that are due. Returns how many were queued.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsActive)
            return 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var plan = _planner.Tick(await LoadAsync(cancellationToken).ConfigureAwait(false), signals.Read(), now);
            await SaveAsync(plan.State, cancellationToken).ConfigureAwait(false); // before any send: nothing is queued twice

            foreach (var (run, outcome) in plan.Expired)
            {
                var why = outcome == AgentRunOutcome.NotRun ? "its job never claimed it" : "its job never reported how it ended";
                LogExpired(logger, run.Id, why);
                await JournalAsync(run, outcome.Name(), Inv($"{Label(run)} {run.Id} given up: {why}; counted as {outcome.Name()}")).ConfigureAwait(false);
            }

            foreach (var notice in plan.Notices)
            {
                LogNotice(logger, notice);
                await JournalAsync(null, "capped", notice).ConfigureAwait(false);
            }

            var sent = 0;
            foreach (var run in plan.Dispatch)
            {
                if (await TrySendAsync(run, cancellationToken).ConfigureAwait(false))
                    sent++;
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A job takes its run before it starts Claude. Null when the run isn't wanted: unknown, for another job, claimed
    /// already, or queued too long ago. A repeated or stale message costs one container start and no Claude usage.
    /// </summary>
    public async Task<AgentRun?> ClaimAsync(string runId, string? role, CancellationToken cancellationToken)
    {
        if (!ToolCallContext.IsSafeLabel(runId) || AgentRunKinds.ForRole(role) is not { } kind || !IsActive)
            return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (state, run) = _planner.Claim(await LoadAsync(cancellationToken).ConfigureAwait(false), runId, kind, time.GetUtcNow());
            if (run is null)
                return null;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false); // claimed once only, across restarts too
            LogClaimed(logger, run.Id);
            await JournalAsync(run, "claimed", Inv($"{Label(run)} {run.Id} started by its job")).ConfigureAwait(false);
            return run;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How a claimed run ended. False if there is no such claimed run for that job (unknown, or reported already).</summary>
    public async Task<bool> CompleteAsync(string runId, string? role, AgentRunOutcome outcome, CancellationToken cancellationToken)
    {
        if (!ToolCallContext.IsSafeLabel(runId) || AgentRunKinds.ForRole(role) is not { } kind || !IsActive)
            return false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (state, run) = _planner.Complete(await LoadAsync(cancellationToken).ConfigureAwait(false), runId, kind, outcome, time.GetUtcNow());
            if (run is null)
                return false;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, run.Id, outcome.Name());
            await JournalAsync(run, outcome.Name(), Inv($"{Label(run)} {run.Id} ended: {outcome.Name()}")).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Poll, time);
        try
        {
            do
            {
                await TryPollAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TryPollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPollFailed(logger, ex.GetType().Name); // nothing was queued; the next look tries again
        }
    }

    private async Task<bool> TrySendAsync(AgentRun run, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            await queue.SendAsync(run, options.ClaimTimeout, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The job can't have it: count it as not run, so the cap is refunded and the rules try again.
            LogSendFailed(logger, run.Id, ex.GetType().Name);
            var state = _planner.Unsent(_state!, run, time.GetUtcNow());
            Volatile.Write(ref _state, state);
            try
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception saveError) when (saveError is not OperationCanceledException)
            {
                LogSaveFailed(logger, saveError.GetType().Name); // kept in memory; saved with the next change
            }

            await JournalAsync(run, "not-sent", Inv($"{Label(run)} {run.Id} could not be queued ({ex.GetType().Name}); counted as not run")).ConfigureAwait(false);
            return false;
        }

        LogQueued(logger, run.Id, Label(run), run.Purpose);
        await JournalAsync(run, "queued", Inv($"{Label(run)} {run.Id} queued: {Reason(run)}")).ConfigureAwait(false);
        return true;
    }

    private async Task<AgentRunState> LoadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is { } loaded)
            return loaded;
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new AgentRunState();
        _saved = JsonSerializer.Serialize(state, CosmosDb.Json);
        Volatile.Write(ref _state, state);
        return state;
    }

    // Saves only what changed; most looks change nothing. The state in memory moves on only once it is saved.
    private async Task SaveAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, CosmosDb.Json);
        if (json != _saved)
        {
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _saved = json;
        }

        Volatile.Write(ref _state, state);
    }

    private static string Label(AgentRun run) => run.Kind switch
    {
        AgentRunKind.Trader => "Trader run",
        AgentRunKind.Requests => "Owner-request run",
        _ => "Analyst run",
    };

    private static string Reason(AgentRun run) => run.Key.Length > 0 ? $"{run.Purpose} {run.Key}" : run.Purpose;

    private async Task JournalAsync(AgentRun? run, string step, string summary)
    {
        var now = time.GetUtcNow();
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["step"] = step };
        if (run is not null)
        {
            data["job"] = run.Kind.Role();
            data["purpose"] = run.Purpose;
            if (run.Key.Length > 0)
                data["key"] = run.Key;
        }

        try
        {
            await journal.AppendAsync(new JournalEntry(JournalIds.New(now, Kind), now, Kind, Actor, run?.Id, null, summary, data), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalFailed(logger, ex.GetType().Name);
        }
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RunId} queued: {Label}, {Purpose}")]
    private static partial void LogQueued(ILogger logger, string runId, string label, string purpose);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RunId} claimed by its job")]
    private static partial void LogClaimed(ILogger logger, string runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RunId} ended: {Outcome}")]
    private static partial void LogCompleted(ILogger logger, string runId, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RunId} given up: {Why}")]
    private static partial void LogExpired(ILogger logger, string runId, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Notice}")]
    private static partial void LogNotice(ILogger logger, string notice);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RunId} could not be queued ({Error}); counted as not run")]
    private static partial void LogSendFailed(ILogger logger, string runId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent runs not checked ({Error}); trying again at the next look")]
    private static partial void LogPollFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run state could not be saved ({Error})")]
    private static partial void LogSaveFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for an agent-run entry ({Error})")]
    private static partial void LogJournalFailed(ILogger logger, string error);
}

public static class AgentRunRegistration
{
    /// <summary>
    /// The agent-run scheduler, in Queue mode only; Compose keeps its shell loops (Off). Register after the scanner and the
    /// manual requests, and before AddCixCosmos, which replaces the in-memory state store.
    /// </summary>
    public static IServiceCollection AddCixAgentRuns(this IServiceCollection services, AgentRunOptions options, ToolServerAuth auth, bool durableState)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(auth, durableState);
        services.AddSingleton(options);
        if (options.Mode == AgentRunMode.Off)
            return services;

        services.AddSingleton<IAgentRunStateStore>(new InMemoryAgentRunStateStore());
        services.AddSingleton<IAgentRunSignals, EngineAgentRunSignals>();
        services.AddSingleton<IAgentRunQueue>(_ => new StorageAgentRunQueue(options.Queues));
        services.AddSingleton<AgentRunScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentRunScheduler>());
        return services;
    }
}
