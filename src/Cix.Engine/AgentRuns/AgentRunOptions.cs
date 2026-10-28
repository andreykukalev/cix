using System.Text.RegularExpressions;
using Cix.Engine.Tools;

namespace Cix.Engine.AgentRuns;

/// <summary>Who starts agent runs.</summary>
public enum AgentRunMode
{
    /// <summary>The default, for Docker Compose: the shell loops in <c>tools/trader</c> decide when to run, and the engine doesn't.</summary>
    Off,

    /// <summary>Azure: the engine decides and puts each run on its job's Storage queue (ADR-0013).</summary>
    Queue,
}

/// <summary>Which job a run is for. Each has its own queue, managed identity and tools (ADR-0013).</summary>
public enum AgentRunKind
{
    /// <summary>A strategy run: signals, heartbeat or a scheduled cycle (<c>cix-trader</c>).</summary>
    Trader,

    /// <summary>A run that answers the owner's requests and can't place orders of its own (<c>cix-trader-requests</c>).</summary>
    Requests,

    /// <summary>The market analyst (<c>cix-analyst</c>).</summary>
    Analyst,
}

/// <summary>How a run ended, as its job reports it.</summary>
public enum AgentRunOutcome
{
    Done,
    Failed,

    /// <summary>Claude was never used (a usage limit, missing credentials): the run is refunded and retried.</summary>
    NotRun,
}

public static class AgentRunKinds
{
    /// <summary>The agent role of the job that runs this kind: the role its managed identity has on the tool server.</summary>
    public static string Role(this AgentRunKind kind) => kind switch
    {
        AgentRunKind.Trader => "trader",
        AgentRunKind.Requests => ToolCallContext.RequestsAgent,
        AgentRunKind.Analyst => ToolCallContext.AnalystAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind whose job has this role, or null.</summary>
    public static AgentRunKind? ForRole(string? role) => role switch
    {
        "trader" => AgentRunKind.Trader,
        ToolCallContext.RequestsAgent => AgentRunKind.Requests,
        ToolCallContext.AnalystAgent => AgentRunKind.Analyst,
        _ => null,
    };

    public static string Name(this AgentRunOutcome outcome) => outcome switch
    {
        AgentRunOutcome.Done => "done",
        AgentRunOutcome.Failed => "failed",
        AgentRunOutcome.NotRun => "not-run",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static AgentRunOutcome? ParseOutcome(string? value) => value switch
    {
        "done" => AgentRunOutcome.Done,
        "failed" => AgentRunOutcome.Failed,
        "not-run" => AgentRunOutcome.NotRun,
        _ => null,
    };

    /// <summary>Claude's usage or rate limit stopped the run before it did any work.</summary>
    public const string UsageLimit = "usage-limit";

    /// <summary>The Claude credential was missing, malformed or expired.</summary>
    public const string Credentials = "credentials";

    /// <summary>
    /// Why a job says its run didn't start: <see cref="UsageLimit"/> or <see cref="Credentials"/>; null for anything else, so
    /// an unknown reason is dropped rather than journaled.
    /// </summary>
    public static string? ParseNotRunReason(string? value) => value switch
    {
        UsageLimit => UsageLimit,
        Credentials => Credentials,
        _ => null,
    };

    /// <summary>The reason in words, for the journal.</summary>
    public static string NotRunReasonText(string reason) => reason switch
    {
        UsageLimit => "Claude usage limit",
        Credentials => "Claude credentials",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>
/// Bound from "AgentRuns". The run rules of <c>tools/trader/run-trader.sh</c> and <c>run-analyst.sh</c>, with the same
/// defaults, for when the engine schedules the runs (ADR-0013).
/// </summary>
public sealed class AgentRunOptions
{
    public const string SectionName = "AgentRuns";

    /// <summary>A run that never used Claude is refunded at most this often a day, so a misjudged failure can't run unbounded.</summary>
    public const int MaxRefundsPerDay = 96;

    /// <summary>The agent jobs' replica timeout in Container Apps (ADR-0013): a job is stopped after this many minutes.</summary>
    public const int JobReplicaTimeoutMinutes = 20;

    /// <summary>A failed or capped owner-request run is tried again after this long.</summary>
    public static readonly TimeSpan RequestsRetry = TimeSpan.FromMinutes(5);

    public AgentRunMode Mode { get; set; } = AgentRunMode.Off;

    /// <summary>Strategies that don't use the scanner: a run every this many minutes.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Signal-driven strategies: a position review this long after the last run with nothing new.</summary>
    public int HeartbeatMinutes { get; set; } = 240;

    /// <summary>Signal-driven strategies: new signals start a run at most this often.</summary>
    public int MinGapMinutes { get; set; } = 15;

    /// <summary>Strategy runs of any kind per UTC day.</summary>
    public int MaxRunsPerDay { get; set; } = 30;

    /// <summary>Owner-request runs per UTC day, counted apart so requests can't use up the strategy's runs.</summary>
    public int MaxRequestRunsPerDay { get; set; } = 20;

    /// <summary>How often the engine looks at the signals and the owner's requests.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Whether to run the market analyst.</summary>
    public bool Analyst { get; set; } = true;

    /// <summary>Hours between analyst runs; at most 12, because a market view lasts at most 12 hours.</summary>
    public int AnalystIntervalHours { get; set; } = 6;

    /// <summary>An analyst run that never used Claude is tried again after this long.</summary>
    public int AnalystRetryMinutes { get; set; } = 30;

    /// <summary>
    /// A run its job hasn't claimed this long after it was queued is dropped and counts as not run. The queue message
    /// expires at the same time, so a job that comes back later finds nothing old to do.
    /// </summary>
    public int ClaimTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// A claimed run that hasn't reported how it ended this long after the claim counts as failed. Keep it above the jobs'
    /// replica timeout (<see cref="JobReplicaTimeoutMinutes"/>), so a run is never given up on while its job may still be working.
    /// </summary>
    public int RunTimeoutMinutes { get; set; } = 25;

    public AgentRunQueueOptions Queues { get; set; } = new();

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public TimeSpan Heartbeat => TimeSpan.FromMinutes(HeartbeatMinutes);

    public TimeSpan MinGap => TimeSpan.FromMinutes(MinGapMinutes);

    public TimeSpan Poll => TimeSpan.FromSeconds(PollSeconds);

    public TimeSpan AnalystInterval => TimeSpan.FromHours(AnalystIntervalHours);

    public TimeSpan AnalystRetry => TimeSpan.FromMinutes(AnalystRetryMinutes);

    /// <summary>Quick analyst retries never add up to more than one interval; then it waits for its normal slot.</summary>
    public int MaxAnalystRetries => AnalystIntervalHours * 60 / AnalystRetryMinutes;

    public TimeSpan ClaimTimeout => TimeSpan.FromMinutes(ClaimTimeoutMinutes);

    public TimeSpan RunTimeout => TimeSpan.FromMinutes(RunTimeoutMinutes);

    /// <param name="auth">How the tool server identifies callers: queued runs need Entra, since the jobs come over the network.</param>
    /// <param name="durableState">Whether the run state survives a restart (Cosmos DB), so the daily caps do too.</param>
    public void Validate(ToolServerAuth auth, bool durableState)
    {
        Positive(IntervalMinutes, nameof(IntervalMinutes));
        Positive(HeartbeatMinutes, nameof(HeartbeatMinutes));
        Positive(MinGapMinutes, nameof(MinGapMinutes));
        Positive(MaxRunsPerDay, nameof(MaxRunsPerDay));
        Positive(MaxRequestRunsPerDay, nameof(MaxRequestRunsPerDay));
        Positive(AnalystRetryMinutes, nameof(AnalystRetryMinutes));
        Positive(ClaimTimeoutMinutes, nameof(ClaimTimeoutMinutes));
        Positive(RunTimeoutMinutes, nameof(RunTimeoutMinutes));
        if (RunTimeoutMinutes <= JobReplicaTimeoutMinutes)
            throw new InvalidOperationException($"AgentRuns:RunTimeoutMinutes must be more than {JobReplicaTimeoutMinutes}, the jobs' replica timeout, so a run is never given up on while its job may still be working.");
        if (PollSeconds is < 5 or > 600)
            throw new InvalidOperationException("AgentRuns:PollSeconds must be between 5 and 600.");
        if (AnalystIntervalHours is < 1 or > 12)
            throw new InvalidOperationException("AgentRuns:AnalystIntervalHours must be between 1 and 12 (a market view lasts at most 12 hours).");
        if (AnalystRetryMinutes > AnalystIntervalHours * 60)
            throw new InvalidOperationException("AgentRuns:AnalystRetryMinutes can't be longer than the analyst interval.");
        if (Mode == AgentRunMode.Off)
            return;

        if (auth != ToolServerAuth.Entra)
            throw new InvalidOperationException("AgentRuns:Mode Queue needs ToolServer:Auth Entra: the jobs reach the engine over the network.");
        if (!durableState)
            throw new InvalidOperationException("AgentRuns:Mode Queue needs Cosmos DB, so the daily caps and runs in flight survive a restart.");
        Queues.Validate();
    }

    private static void Positive(int value, string name)
    {
        if (value < 1)
            throw new InvalidOperationException($"AgentRuns:{name} must be a positive whole number.");
    }
}

/// <summary>Bound from "AgentRuns:Queues": the Storage queues the engine puts runs on (it needs only send rights on them).</summary>
public sealed partial class AgentRunQueueOptions
{
    /// <summary>The storage account's queue endpoint, for example <c>https://cixruns.queue.core.windows.net</c>.</summary>
    public string? ServiceUri { get; set; }

    public string Trader { get; set; } = "runs-trader";

    public string Requests { get; set; } = "runs-requests";

    public string Analyst { get; set; } = "runs-analyst";

    /// <summary>The engine's user-assigned managed identity (its client ID); empty for a system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public string QueueFor(AgentRunKind kind) => kind switch
    {
        AgentRunKind.Trader => Trader,
        AgentRunKind.Requests => Requests,
        AgentRunKind.Analyst => Analyst,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public void Validate()
    {
        if (!Uri.TryCreate(ServiceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.PathAndQuery != "/")
            throw new InvalidOperationException("AgentRuns:Queues:ServiceUri must be the storage account's https queue endpoint, with no path or query.");
        string[] names = [Trader, Requests, Analyst];
        if (names.Any(n => n is null || !QueueName().IsMatch(n)))
            throw new InvalidOperationException("AgentRuns:Queues names must be 3 to 63 lowercase letters, digits and single hyphens.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidOperationException("AgentRuns:Queues must name a different queue for each job.");
        if (!string.IsNullOrEmpty(ManagedIdentityClientId) && !Guid.TryParse(ManagedIdentityClientId, out _))
            throw new InvalidOperationException("AgentRuns:Queues:ManagedIdentityClientId must be the identity's client ID (a GUID).");
    }

    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex QueueName();
}
