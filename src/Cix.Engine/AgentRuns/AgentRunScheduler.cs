using System.Globalization;
using System.Text.Json;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cix.Engine.AgentRuns;

/// <summary>Where runs go: one queue per job. A fake in tests, Storage queues in Azure.</summary>
public interface IAgentRunQueue
{
    /// <summary>Queues the run for its job; the message expires after <paramref name="timeToLive"/>.</summary>
    Task SendAsync(AgentRun run, TimeSpan timeToLive, CancellationToken cancellationToken);
}

/// <summary>The saved state and its version.</summary>
public sealed record StoredAgentRunState(AgentRunState State, string ETag);

/// <summary>
/// The saved <see cref="AgentRunState"/>: in memory by default, Cosmos DB with AddCixCosmos. Writes are conditional on the
/// version read, so an engine that lost the engine lease can't overwrite what the engine that took over saved.
/// </summary>
public interface IAgentRunStateStore
{
    Task<StoredAgentRunState?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves if the document is still at <paramref name="etag"/> (or, with null, doesn't exist yet). Returns the new version,
    /// or null if someone else wrote it since.
    /// </summary>
    Task<string?> SaveAsync(AgentRunState state, string? etag, CancellationToken cancellationToken);
}

public sealed class InMemoryAgentRunStateStore : IAgentRunStateStore
{
    private readonly object _gate = new();
    private StoredAgentRunState? _saved;
    private int _version;

    public int Saves { get; private set; }

    public Task<StoredAgentRunState?> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_saved);
    }

    public Task<string?> SaveAsync(AgentRunState state, string? etag, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_saved?.ETag != etag)
                return Task.FromResult<string?>(null);
            _saved = new StoredAgentRunState(state, (++_version).ToString(CultureInfo.InvariantCulture));
            Saves++;
            return Task.FromResult<string?>(_saved.ETag);
        }
    }
}

/// <summary>How the engine answers a job.</summary>
public enum AgentRunAnswer
{
    Yes,
    No,

    /// <summary>Not this engine's call right now: it doesn't hold the lease, is busy, or another engine wrote the state. Ask again shortly.</summary>
    Unavailable,
}

/// <summary>This engine may not change the run state now: it lost the lease, or another engine saved it first.</summary>
public sealed class AgentRunsUnavailableException(string message) : Exception(message);

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
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A job waits at most this long for a look in progress; then it is told to ask again.</summary>
    public static readonly TimeSpan JobWait = TimeSpan.FromSeconds(10);

    private readonly AgentRunOptions _options = options;
    private readonly AgentRunPlanner _planner = new(options);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _usedRuns = new(StringComparer.Ordinal);
    private AgentRunState? _state;
    private string? _etag;
    private string? _saved;

    /// <summary>Whether this engine schedules runs now: only while it holds the engine lease.</summary>
    public bool IsActive => lease is not { IsHeld: false };

    /// <summary>The state as last saved, or null before it is loaded.</summary>
    public AgentRunState? State => Volatile.Read(ref _state);

    /// <summary>
    /// Whether <paramref name="runId"/> is a run of <paramref name="role"/>'s job that its job claimed and hasn't reported:
    /// the only runs whose tool calls the tool server accepts in Azure. Never on an engine without the lease, whose state
    /// may be stale. From memory, so a tool call costs no database read.
    /// </summary>
    public bool IsRunInProgress(string? runId, string? role) =>
        IsActive && runId is not null && AgentRunKinds.ForRole(role) is { } kind
        && State?.InFlight.Any(r => r.Id == runId && r.Kind == kind && r.ClaimedAt is not null) == true;

    /// <summary>
    /// Whether a trader run (strategy or owner requests) has been claimed and hasn't ended: a strategy switch waits for it, so
    /// one run never works under two specs. From memory.
    /// </summary>
    public bool TraderRunInProgress =>
        IsActive && State?.InFlight.Any(r => r.Kind != AgentRunKind.Analyst && r.ClaimedAt is not null) == true;

    /// <summary>
    /// Notes that a run in progress made a tool call, so a later <c>not-run</c> report for it counts as failed: a run that
    /// used tools can't be refunded, whatever its job says. In memory: after a restart, a report is taken as given.
    /// </summary>
    public void NoteToolCall(string runId)
    {
        if (_usedRuns.Count > 64 && State is { } state) // runs that expired without a report: keep only those still in flight
            foreach (var id in _usedRuns.Keys.Where(id => state.InFlight.All(r => r.Id != id)))
                _usedRuns.TryRemove(id, out _);
        _usedRuns.TryAdd(runId, 0);
    }

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
                if (!IsActive)
                    break; // the lease went: what isn't sent expires unclaimed and counts as not run
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
    /// A job takes its run before it starts Claude. No when the run isn't wanted: unknown, for another job, claimed
    /// already by another execution, or queued too long ago. A repeated or stale message costs one container start and no
    /// Claude usage. <paramref name="claimant"/> (the job execution's name) lets that execution repeat a claim whose answer
    /// it didn't get.
    /// </summary>
    public async Task<(AgentRunAnswer Answer, AgentRun? Run)> ClaimAsync(string runId, string? role, string? claimant, CancellationToken cancellationToken)
    {
        if (!ToolCallContext.IsSafeLabel(runId) || AgentRunKinds.ForRole(role) is not { } kind || (claimant is not null && !ToolCallContext.IsSafeLabel(claimant)))
            return (AgentRunAnswer.No, null);
        if (!IsActive || !await _gate.WaitAsync(JobWait, cancellationToken).ConfigureAwait(false))
            return (AgentRunAnswer.Unavailable, null);
        try
        {
            var (state, run, repeated) = _planner.Claim(await LoadAsync(cancellationToken).ConfigureAwait(false), runId, kind, claimant, time.GetUtcNow());
            if (run is null)
                return (AgentRunAnswer.No, null);
            if (repeated)
                return (AgentRunAnswer.Yes, run);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false); // claimed once only, across restarts too
            LogClaimed(logger, run.Id);
            await JournalAsync(run, "claimed", Inv($"{Label(run)} {run.Id} started by its job")).ConfigureAwait(false);
            return (AgentRunAnswer.Yes, run);
        }
        catch (AgentRunsUnavailableException)
        {
            return (AgentRunAnswer.Unavailable, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How a claimed run ended. No if there is no such claimed run for that job (unknown, or reported already).</summary>
    public Task<AgentRunAnswer> CompleteAsync(string runId, string? role, AgentRunOutcome outcome, CancellationToken cancellationToken) =>
        CompleteAsync(runId, role, outcome, null, cancellationToken);

    /// <summary>
    /// How a claimed run ended, and for a not-run why it didn't start (<see cref="AgentRunKinds.ParseNotRunReason"/>). The
    /// reason is journaled only when the run counts as not run, so the dashboard can show a Claude limit apart from a failure.
    /// </summary>
    public async Task<AgentRunAnswer> CompleteAsync(string runId, string? role, AgentRunOutcome outcome, string? notRunReason, CancellationToken cancellationToken)
    {
        if (!ToolCallContext.IsSafeLabel(runId) || AgentRunKinds.ForRole(role) is not { } kind)
            return AgentRunAnswer.No;
        if (!IsActive || !await _gate.WaitAsync(JobWait, cancellationToken).ConfigureAwait(false))
            return AgentRunAnswer.Unavailable;
        try
        {
            var reported = outcome;
            if (outcome == AgentRunOutcome.NotRun && _usedRuns.ContainsKey(runId))
                outcome = AgentRunOutcome.Failed; // it used tools, so it ran: no refund
            var (state, run) = _planner.Complete(await LoadAsync(cancellationToken).ConfigureAwait(false), runId, kind, outcome, time.GetUtcNow());
            if (run is null)
                return AgentRunAnswer.No;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _usedRuns.TryRemove(runId, out _);
            LogCompleted(logger, run.Id, outcome.Name());
            var reason = outcome == AgentRunOutcome.NotRun ? AgentRunKinds.ParseNotRunReason(notRunReason) : null;
            var note = reported != outcome ? $" (reported {reported.Name()}, but it made tool calls)"
                : reason is not null ? $" ({AgentRunKinds.NotRunReasonText(reason)})" : string.Empty;
            await JournalAsync(run, outcome.Name(), Inv($"{Label(run)} {run.Id} ended: {outcome.Name()}{note}"), reason).ConfigureAwait(false);
            return AgentRunAnswer.Yes;
        }
        catch (AgentRunsUnavailableException)
        {
            return AgentRunAnswer.Unavailable;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The owner's Run now button (<see cref="AgentRunPlanner.RunAnalystNow"/>): queues an analyst run at once. Yes with what
    /// was queued, No with why not, or Unavailable when this engine can't decide now (ask again at the next poll).
    /// </summary>
    public async Task<(AgentRunAnswer Answer, string Detail)> RunAnalystNowAsync(CancellationToken cancellationToken)
    {
        const string Unavailable = "This engine can't queue runs right now.";
        if (!IsActive || !await _gate.WaitAsync(JobWait, cancellationToken).ConfigureAwait(false))
            return (AgentRunAnswer.Unavailable, Unavailable);
        try
        {
            var (state, run, refusal) = _planner.RunAnalystNow(await LoadAsync(cancellationToken).ConfigureAwait(false), time.GetUtcNow());
            if (run is null)
                return (AgentRunAnswer.No, refusal!);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false); // before the send, as for every run
            return await TrySendAsync(run, cancellationToken).ConfigureAwait(false)
                ? (AgentRunAnswer.Yes, Inv($"Analyst run {run.Id} queued; its market view usually arrives within 10 minutes. The next scheduled run is {_options.AnalystIntervalHours} hours after this one."))
                : (AgentRunAnswer.No, "The analyst run couldn't be queued; press the button again in a minute.");
        }
        catch (AgentRunsUnavailableException)
        {
            return (AgentRunAnswer.Unavailable, Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Poll, time);
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
            await queue.SendAsync(run, _options.ClaimTimeout, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The job can't have it: count it as not run, so the cap is refunded and the rules try again.
            LogSendFailed(logger, run.Id, ex.GetType().Name);
            try
            {
                if (Volatile.Read(ref _state) is { } current)
                    await SaveAsync(_planner.Unsent(current, run, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception saveError) when (saveError is not OperationCanceledException)
            {
                LogSaveFailed(logger, saveError.GetType().Name); // it stays in flight and expires unclaimed: also not run
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
        var stored = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var state = stored?.State ?? new AgentRunState();
        _etag = stored?.ETag;
        _saved = stored is null ? null : JsonSerializer.Serialize(state, CosmosDb.Json);
        Volatile.Write(ref _state, state);
        return state;
    }

    // Saves only what changed; most looks change nothing. The state in memory moves on only once it is saved, and only by
    // this engine while it holds the lease and nobody else wrote the state since it read it.
    private async Task SaveAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, CosmosDb.Json);
        if (json != _saved)
        {
            if (!IsActive)
                throw new AgentRunsUnavailableException("This engine no longer holds the engine lease.");
            var etag = await store.SaveAsync(state, _etag, cancellationToken).ConfigureAwait(false);
            if (etag is null)
            {
                Volatile.Write(ref _state, null); // read it again at the next look
                LogConflict(logger);
                throw new AgentRunsUnavailableException("Another engine saved the agent run state.");
            }

            _etag = etag;
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

    private async Task JournalAsync(AgentRun? run, string step, string summary, string? reason = null)
    {
        var now = time.GetUtcNow();
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["step"] = step };
        if (reason is not null)
            data["reason"] = reason;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another engine saved the agent run state; this one reads it again")]
    private static partial void LogConflict(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for an agent-run entry ({Error})")]
    private static partial void LogJournalFailed(ILogger logger, string error);
}

public static class AgentRunRegistration
{
    /// <summary>
    /// The agent-run scheduler, in Queue mode only; Compose keeps its shell loops (Off). Register after the scanner and the
    /// manual requests (the Run now requests share their container), and before AddCixCosmos, which replaces the in-memory
    /// state store.
    /// </summary>
    public static IServiceCollection AddCixAgentRuns(this IServiceCollection services, AgentRunOptions options, ToolServerAuth auth, bool durableState)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(auth, durableState);
        services.AddSingleton(options);

        // The owner's Run now button, in both modes: without the scheduler (Off) each request is refused and says why.
        services.AddSingleton<AnalystRunRequestService>();
        services.AddHostedService(sp => sp.GetRequiredService<AnalystRunRequestService>());
        if (options.Mode == AgentRunMode.Off)
            return services;

        services.TryAddSingleton<IAgentRunStateStore>(new InMemoryAgentRunStateStore()); // AddCixCosmos's store wins either way
        services.AddSingleton<IAgentRunSignals, EngineAgentRunSignals>();
        services.AddSingleton<IAgentRunQueue>(_ => new StorageAgentRunQueue(options.Queues));
        services.AddSingleton<AgentRunScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentRunScheduler>());
        return services;
    }
}
