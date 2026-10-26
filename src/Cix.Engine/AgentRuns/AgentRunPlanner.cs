using System.Globalization;

namespace Cix.Engine.AgentRuns;

/// <summary>Why a run was started.</summary>
public static class AgentRunPurpose
{
    /// <summary>The scanner's signals hold something new.</summary>
    public const string Signals = "signals";

    /// <summary>A signal-driven strategy's slow position review.</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>A strategy without the scanner, on its fixed cadence.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The owner's requests are waiting for the trader.</summary>
    public const string Requests = "requests";

    /// <summary>The analyst's slot, or its retry.</summary>
    public const string Analyst = "analyst";
}

/// <summary>A run the engine queued and hasn't heard the end of.</summary>
/// <param name="Id">The run ID the job sends to the tool server (<c>X-Cix-Run</c>), so the journal links its calls to it.</param>
/// <param name="Key">The signal key or request key it was started for; empty for heartbeat, scheduled and analyst runs.</param>
/// <param name="ClaimedAt">When its job claimed it; a run is claimed once only.</param>
public sealed record AgentRun(string Id, AgentRunKind Kind, string Purpose, string Key, DateTimeOffset DispatchedAt, DateTimeOffset? ClaimedAt = null)
{
    /// <summary><c>run-20261026-140500-3fa2c1</c>, or <c>analyst-...</c>: readable, ordered, and a safe journal label.</summary>
    public static string NewId(AgentRunKind kind, DateTimeOffset at) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(kind == AgentRunKind.Analyst ? "analyst" : "run")}-{at.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}")[..^26];
}

/// <summary>
/// Everything the run rules remember, saved after every change and before any run is queued, so a restarted or new engine
/// carries on with the same caps, keys and runs in flight.
/// </summary>
public sealed record AgentRunState
{
    /// <summary>The UTC day the counts are for.</summary>
    public DateOnly Day { get; init; }

    /// <summary>Strategy runs queued today, less refunds.</summary>
    public int Runs { get; init; }

    public int Refunds { get; init; }

    public int RequestRuns { get; init; }

    /// <summary>The day the strategy cap was last reported, so it is reported once a day.</summary>
    public DateOnly? CappedOn { get; init; }

    public DateOnly? RequestsCappedOn { get; init; }

    /// <summary>The signal key last handled: a run starts only for an item that isn't in it.</summary>
    public string LastKey { get; init; } = string.Empty;

    /// <summary>When the last strategy run was queued: new signals wait for the minimum gap after it.</summary>
    public DateTimeOffset? LastRun { get; init; }

    public DateTimeOffset? NextHeartbeat { get; init; }

    /// <summary>Strategies without the scanner: the next run. Null while the strategy is signal-driven.</summary>
    public DateTimeOffset? NextScheduled { get; init; }

    /// <summary>The request key last handled.</summary>
    public string LastRequests { get; init; } = string.Empty;

    public DateTimeOffset? RequestsRetryAt { get; init; }

    /// <summary>The analyst's cadence: each slot follows the previous one, with no catch-up runs.</summary>
    public DateTimeOffset? AnalystSlot { get; init; }

    public DateTimeOffset? NextAnalyst { get; init; }

    public int AnalystRetries { get; init; }

    /// <summary>At most one run of each kind at a time, like the jobs (<c>maxExecutions: 1</c>).</summary>
    public IReadOnlyList<AgentRun> InFlight { get; init; } = [];

    public AgentRun? InFlightOf(AgentRunKind kind) => InFlight.FirstOrDefault(r => r.Kind == kind);
}

/// <summary>What the engine knows right now.</summary>
/// <param name="Scanned">Whether the scanner has finished a scan, so signal runs see real signals.</param>
/// <param name="SignalDriven">Whether the active strategy uses the scanner (scan_market).</param>
/// <param name="SignalKey">The latest scan's signals, like <c>buy:SOL-EUR|sell:BTC-EUR:stop</c>.</param>
/// <param name="RequestsKey">The owner's requests waiting for the trader, like <c>manual:&lt;id&gt;|...</c>.</param>
public sealed record AgentRunInputs(bool Scanned, bool SignalDriven, string SignalKey, string RequestsKey);

/// <summary>A run given up on: never claimed (not run) or never reported (failed).</summary>
public sealed record ExpiredRun(AgentRun Run, AgentRunOutcome Outcome);

/// <summary>The result of one look: the new state, the runs to queue, and what to tell the owner.</summary>
public sealed record AgentRunPlan(AgentRunState State, IReadOnlyList<AgentRun> Dispatch, IReadOnlyList<ExpiredRun> Expired, IReadOnlyList<string> Notices);

/// <summary>
/// The run rules of <c>tools/trader/run-trader.sh</c> and <c>run-analyst.sh</c> as pure functions over
/// <see cref="AgentRunState"/>, so they can be tested without clocks, queues or Claude. The loops ran each agent inline and
/// waited; here a run is queued, and its end arrives later through <see cref="Complete"/>.
/// </summary>
public sealed class AgentRunPlanner(AgentRunOptions options)
{
    /// <summary>One look at the signals and requests: expires stale runs, then decides what to queue.</summary>
    public AgentRunPlan Tick(AgentRunState state, AgentRunInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);
        var s = RollDay(state, now);
        var dispatch = new List<AgentRun>();
        var expired = new List<ExpiredRun>();
        var notices = new List<string>();

        foreach (var run in s.InFlight.ToList())
        {
            AgentRunOutcome? outcome = run.ClaimedAt is { } claimed
                ? (now - claimed >= options.RunTimeout ? AgentRunOutcome.Failed : (AgentRunOutcome?)null)
                : (now - run.DispatchedAt >= options.ClaimTimeout ? AgentRunOutcome.NotRun : (AgentRunOutcome?)null);
            if (outcome is { } ended)
            {
                s = Apply(s, run, ended, now);
                expired.Add(new ExpiredRun(run, ended));
            }
        }

        s = PlanRequests(s, inputs, now, dispatch, notices);
        s = PlanTrader(s, inputs, now, dispatch, notices);
        s = PlanAnalyst(s, now, dispatch);
        return new AgentRunPlan(s, dispatch, expired, notices);
    }

    /// <summary>The job took the run. Null if it isn't in flight for that kind, was claimed already, or waited too long.</summary>
    public (AgentRunState State, AgentRun? Run) Claim(AgentRunState state, string runId, AgentRunKind kind, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var run = state.InFlight.FirstOrDefault(r => r.Id == runId && r.Kind == kind);
        if (run is null || run.ClaimedAt is not null || now - run.DispatchedAt >= options.ClaimTimeout)
            return (state, null);
        var claimed = run with { ClaimedAt = now };
        return (state with { InFlight = state.InFlight.Select(r => r.Id == runId ? claimed : r).ToList() }, claimed);
    }

    /// <summary>
    /// How a claimed run ended. Null if it isn't a claimed run of that kind in flight (unknown, already reported, or given
    /// up on), so a repeated report changes nothing.
    /// </summary>
    public (AgentRunState State, AgentRun? Run) Complete(AgentRunState state, string runId, AgentRunKind kind, AgentRunOutcome outcome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var run = state.InFlight.FirstOrDefault(r => r.Id == runId && r.Kind == kind && r.ClaimedAt is not null);
        return run is null ? (state, null) : (Apply(RollDay(state, now), run, outcome, now), run);
    }

    /// <summary>A queued run whose message couldn't be sent: it never ran.</summary>
    public AgentRunState Unsent(AgentRunState state, AgentRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(run);
        return state.InFlight.Any(r => r.Id == run.Id) ? Apply(RollDay(state, now), run, AgentRunOutcome.NotRun, now) : state;
    }

    private static AgentRunState RollDay(AgentRunState s, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return today == s.Day ? s : s with { Day = today, Runs = 0, Refunds = 0, RequestRuns = 0 };
    }

    // Owner requests first: the owner is waiting, and each request expires in the engine if it isn't answered in time.
    private AgentRunState PlanRequests(AgentRunState s, AgentRunInputs inputs, DateTimeOffset now, List<AgentRun> dispatch, List<string> notices)
    {
        if (inputs.RequestsKey.Length == 0 || !HasNew(inputs.RequestsKey, s.LastRequests))
            return s with { LastRequests = inputs.RequestsKey }; // remember the smaller set, so a request coming back counts as new
        if (s.InFlightOf(AgentRunKind.Requests) is not null || (s.RequestsRetryAt is { } retryAt && now < retryAt))
            return s;
        if (s.RequestRuns >= options.MaxRequestRunsPerDay)
        {
            if (s.RequestsCappedOn != s.Day)
            {
                notices.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Daily cap of {options.MaxRequestRunsPerDay} owner-request runs reached; waiting requests expire in the engine (AgentRuns:MaxRequestRunsPerDay)."));
                s = s with { RequestsCappedOn = s.Day };
            }

            return s with { RequestsRetryAt = now + AgentRunOptions.RequestsRetry };
        }

        var run = new AgentRun(AgentRun.NewId(AgentRunKind.Requests, now), AgentRunKind.Requests, AgentRunPurpose.Requests, inputs.RequestsKey, now);
        dispatch.Add(run);
        return s with { RequestRuns = s.RequestRuns + 1, InFlight = [.. s.InFlight, run] };
    }

    private AgentRunState PlanTrader(AgentRunState s, AgentRunInputs inputs, DateTimeOffset now, List<AgentRun> dispatch, List<string> notices)
    {
        if (!inputs.SignalDriven)
        {
            // A strategy without the scanner: a fixed cadence, no catch-up bursts. Coming from a signal-driven strategy, the
            // first scheduled run waits an interval after the last run.
            var next = s.NextScheduled ?? (s.LastRun is { } last && last + options.Interval > now ? last + options.Interval : now);
            s = s with { NextScheduled = next };
            if (s.InFlightOf(AgentRunKind.Trader) is not null || now < next)
                return s;
            s = TryStrategyRun(s, AgentRunPurpose.Scheduled, string.Empty, now, dispatch, notices);
            var following = next + options.Interval;
            return s with { NextScheduled = following <= now ? now + options.Interval : following };
        }

        s = s with { NextScheduled = null };
        if (!inputs.Scanned)
            return s; // the first run should see real signals
        if (s.NextHeartbeat is null)
            s = s with { NextHeartbeat = now };
        if (s.InFlightOf(AgentRunKind.Trader) is not null)
            return s;

        if (inputs.SignalKey.Length > 0 && HasNew(inputs.SignalKey, s.LastKey))
        {
            // Something new, not just one of the previous signals going away. Within the minimum gap it waits: it is still new
            // at the first look after the gap. A run that fails leaves it unhandled, so it is tried again after the gap.
            return s.LastRun is { } last && now - last < options.MinGap
                ? s
                : TryStrategyRun(s, AgentRunPurpose.Signals, inputs.SignalKey, now, dispatch, notices);
        }

        s = s with { LastKey = inputs.SignalKey }; // only removals: a removed signal coming back counts as new
        if (now < s.NextHeartbeat)
            return s;
        s = TryStrategyRun(s, AgentRunPurpose.Heartbeat, string.Empty, now, dispatch, notices);
        return s with { NextHeartbeat = now + options.Heartbeat }; // capped or not; a run moves it again when it ends
    }

    // Every strategy run goes through here: the daily cap applies to signal, heartbeat and scheduled runs alike.
    private AgentRunState TryStrategyRun(AgentRunState s, string purpose, string key, DateTimeOffset now, List<AgentRun> dispatch, List<string> notices)
    {
        if (s.Runs >= options.MaxRunsPerDay)
        {
            if (s.CappedOn == s.Day)
                return s;
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"Daily cap of {options.MaxRunsPerDay} trader runs reached; no more until 00:00 UTC (AgentRuns:MaxRunsPerDay)."));
            return s with { CappedOn = s.Day };
        }

        var run = new AgentRun(AgentRun.NewId(AgentRunKind.Trader, now), AgentRunKind.Trader, purpose, key, now);
        dispatch.Add(run);
        return s with { Runs = s.Runs + 1, LastRun = now, InFlight = [.. s.InFlight, run] };
    }

    private AgentRunState PlanAnalyst(AgentRunState s, DateTimeOffset now, List<AgentRun> dispatch)
    {
        if (!options.Analyst || s.InFlightOf(AgentRunKind.Analyst) is not null || (s.NextAnalyst is { } next && now < next))
            return s;
        var run = new AgentRun(AgentRun.NewId(AgentRunKind.Analyst, now), AgentRunKind.Analyst, AgentRunPurpose.Analyst, string.Empty, now);
        dispatch.Add(run);
        return s with { AnalystSlot = s.AnalystSlot ?? now, InFlight = [.. s.InFlight, run] }; // the first run is at start
    }

    private AgentRunState Apply(AgentRunState s, AgentRun run, AgentRunOutcome outcome, DateTimeOffset now)
    {
        s = s with { InFlight = s.InFlight.Where(r => r.Id != run.Id).ToList() };
        var today = DateOnly.FromDateTime(run.DispatchedAt.UtcDateTime) == s.Day; // a refund belongs to the day the run was counted on
        switch (run.Kind)
        {
            case AgentRunKind.Requests:
                if (outcome == AgentRunOutcome.Done)
                    return s with { LastRequests = run.Key, RequestsRetryAt = null };
                return s with
                {
                    RequestRuns = outcome == AgentRunOutcome.NotRun && today ? Math.Max(0, s.RequestRuns - 1) : s.RequestRuns,
                    RequestsRetryAt = now + AgentRunOptions.RequestsRetry,
                };

            case AgentRunKind.Trader:
                // Claude wasn't used: don't spend the daily cap, up to a ceiling so a misjudged failure can't run unbounded.
                if (outcome == AgentRunOutcome.NotRun && today && s.Refunds < AgentRunOptions.MaxRefundsPerDay)
                    s = s with { Runs = Math.Max(0, s.Runs - 1), Refunds = s.Refunds + 1 };
                if (run.Purpose == AgentRunPurpose.Signals && outcome == AgentRunOutcome.Done)
                    return s with { LastKey = run.Key, NextHeartbeat = now + options.Heartbeat };
                return run.Purpose == AgentRunPurpose.Heartbeat ? s with { NextHeartbeat = now + options.Heartbeat } : s;

            case AgentRunKind.Analyst:
                // A run that couldn't use Claude is retried soon; the slot it missed isn't made up twice.
                if (outcome == AgentRunOutcome.NotRun && s.AnalystRetries < options.MaxAnalystRetries)
                    return s with { AnalystRetries = s.AnalystRetries + 1, AnalystSlot = now, NextAnalyst = now + options.AnalystRetry };
                var slot = (s.AnalystSlot ?? run.DispatchedAt) + options.AnalystInterval;
                if (slot <= now)
                    slot = now + options.AnalystInterval;
                return s with { AnalystRetries = 0, AnalystSlot = slot, NextAnalyst = slot };

            default:
                return s;
        }
    }

    /// <summary>Whether <paramref name="key"/> (items joined by <c>|</c>) holds an item that <paramref name="previous"/> doesn't.</summary>
    public static bool HasNew(string key, string previous)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(previous);
        var seen = previous.Split('|').ToHashSet(StringComparer.Ordinal);
        return key.Split('|').Any(item => item.Length > 0 && !seen.Contains(item));
    }
}
