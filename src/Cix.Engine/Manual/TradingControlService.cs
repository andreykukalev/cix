using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Engine.Execution;
using Cix.Engine.Persistence;

namespace Cix.Engine.Manual;

/// <summary>
/// The owner's Pause and Resume buttons on the dashboard (ADR-0018), in either trading mode: <see cref="RequestType"/>
/// documents in the <c>requests</c> container, which the dashboard can only create. The newest valid request acts and older
/// ones are superseded. Pause engages the kill switch as the owner; Resume releases it, as the operator tool
/// <c>resume_trading</c> does, but only the halt the page showed when the owner pressed it: the request carries that halt's
/// <c>since</c> and the later cause recorded beside it (<see cref="KillSwitchState.AlsoAt"/>), and any difference is refused,
/// so the owner never releases a halt they haven't seen. The execution service journals each change (kind
/// <c>kill-switch</c>); the request's document records what became of it.
/// </summary>
public sealed partial class TradingControlService(
    IManualRequestStore requests,
    ExecutionService execution,
    KillSwitch killSwitch,
    TradingContext trading,
    TimeProvider time,
    ILogger<TradingControlService> logger,
    IEngineLease? lease = null) : BackgroundService
{
    /// <summary>The <c>type</c> of a Pause or Resume request.</summary>
    public const string RequestType = "trading-control";

    public const string Pause = "pause";
    public const string Resume = "resume";

    /// <summary>Who engages and releases the kill switch for these requests, as for the operator tools.</summary>
    public const string Owner = "owner";

    public const string Actor = "engine";

    /// <summary>Documents handled in one poll; the rest wait for the next.</summary>
    public const int MaxPerPoll = 10;

    /// <summary>Requests acted on in one UTC day; more are refused, so a flood can't keep toggling the switch.</summary>
    public const int MaxActsPerDay = 20;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>A request older than this when the engine first sees it expires instead of acting on a stale intent.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How far a request's timestamp may be ahead of the engine's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How close a halt's time must be to the one the page sent to count as the same halt: the times only pass through the
    /// status document and the page, and a new halt needs a release in between.
    /// </summary>
    public static readonly TimeSpan SameHaltTolerance = TimeSpan.FromSeconds(1);

    /// <summary>What each request acted on in this run became, so one whose document couldn't be updated keeps its outcome.</summary>
    private readonly Dictionary<string, (string Status, string Detail)> _outcomes = new(StringComparer.Ordinal);

    private DateTimeOffset? _lastActedAt;
    private DateOnly _actsDay;
    private int _actsToday;
    private string? _lastError;

    /// <summary>
    /// Takes the new requests for this engine's mode: acts on the newest valid one and records the outcome on each, at most
    /// <see cref="MaxPerPoll"/> a poll. Returns how many documents it handled. Tests call this instead of running the loop.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (lease is { IsHeld: false } || requests.State != "ok")
            return 0; // only the engine that trades acts; without the container there is nothing to read
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        IReadOnlyList<StoredRequest> found;
        try
        {
            found = await requests.ListNewAsync(today.AddDays(-1), today.AddDays(1), trading.Mode.ToString().ToLowerInvariant(), cancellationToken, RequestType)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, ex.GetType().Name);
            return 0;
        }

        if (found.Count == 0)
            return 0;
        var parsed = found.Select(r => (Stored: r, Parsed: Parse(r.Document, now)))
            .OrderBy(p => p.Parsed.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(p => p.Stored.Id, StringComparer.Ordinal).ToList();
        var newest = parsed.LastOrDefault(p => p.Parsed.Action is not null && !_outcomes.ContainsKey(p.Stored.Id)).Stored;

        // The newest first: older ones are superseded only once it has acted, so a failure drops none of them.
        var handled = 0;
        foreach (var (stored, request) in parsed.OrderBy(p => ReferenceEquals(p.Stored, newest) ? 0 : 1).Take(MaxPerPoll))
        {
            string status, detail;
            if (_outcomes.TryGetValue(stored.Id, out var done))
                (status, detail) = done; // acted on already: its own outcome, even once it would have expired
            else if (request.Action is null)
                (status, detail) = (request.Expired ? ManualRequestStatus.Expired : ManualRequestStatus.Invalid, request.Problem!);
            else if (!ReferenceEquals(stored, newest) || request.CreatedAt <= _lastActedAt)
                (status, detail) = ("superseded", "A newer Pause or Resume request replaced this one.");
            else if (await ActAsync(stored.Id, request, now).ConfigureAwait(false) is { } outcome)
                (status, detail) = outcome;
            else
                return handled; // couldn't act: everything stays new and is taken at the next poll

            try
            {
                // A document refused for its shape isn't copied back: only its id, day and type, with the outcome.
                var basis = ManualRequestDocument.ShapeProblem(stored.Document) is null
                    ? stored.Document
                    : new JsonObject { ["id"] = stored.Id, ["day"] = stored.Day, ["type"] = RequestType };
                await requests.TryReplaceAsync(stored, ManualRequestDocument.WithStatus(basis, status, now, Actor, detail), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(logger, ex.GetType().Name); // read again next time; one already acted on isn't acted on again
            }

            handled++;
        }

        return handled;
    }

    /// <summary>Acts on the request once; returns its status and detail, or null when it couldn't act (retried at the next poll).</summary>
    private async Task<(string Status, string Detail)?> ActAsync(string id, ParsedRequest request, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today != _actsDay)
            (_actsDay, _actsToday) = (today, 0);
        var state = killSwitch.State;
        if (_actsToday >= MaxActsPerDay && !(request.Action == Pause && !state.Engaged)) // a Pause that halts trading is never capped
        {
            return Remember(id, request, (ManualRequestStatus.Rejected, string.Create(CultureInfo.InvariantCulture,
                $"At most {MaxActsPerDay} Pause or Resume requests a day reach the engine; this one didn't. To stop live trading now, see docs/live-trading.md.")), counts: false);
        }

        try
        {
            if (request.Action == Pause)
            {
                if (state.Engaged)
                    return Remember(id, request, ("accepted", "Trading was already halted; the halt and its reason stay as they were."));

                // A fixed reason: the halt reason reaches the agents, and the owner's note stays on the request (ADR-0018).
                // Not cancellable once started: the switch must never end up engaged with orders left uncancelled.
                var result = await execution.EngageKillSwitchAsync($"Paused by the owner from the dashboard (request {id}).", Owner, CancellationToken.None)
                    .ConfigureAwait(false);
                LogActed(logger, Pause, ManualRequestDocument.SafeIdOrInvalid(id));
                return Remember(id, request, ("accepted", result.NotCancelled.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Trading halted; {result.Cancelled} open order(s) cancelled.")
                    : string.Create(CultureInfo.InvariantCulture, $"Trading halted; {result.Cancelled} open order(s) cancelled, {result.NotCancelled.Count} couldn't be: check the exchange.")));
            }

            if (!state.Engaged)
                return Remember(id, request, ("accepted", "Trading wasn't halted; nothing to resume."));
            if (state.Since is not { } since)
            {
                return Remember(id, request, (ManualRequestStatus.Rejected,
                    "This halt has no start time, so the engine can't tell it is the one you saw. Resume it with the operator tool resume_trading."));
            }

            if (!SameHalt(since, state.AlsoAt, request))
            {
                // Not the halt the owner saw: a newer one, or a cause found while it was in place, stays until they've seen it.
                return Remember(id, request, (ManualRequestStatus.Rejected,
                    $"The halt changed after the page showed it: {Status.EngineStatusService.HaltCategoryFor(state.AlsoBy ?? state.By)} Check why on the dashboard, then press Resume again if you still want to."));
            }

            await execution.ReleaseKillSwitchAsync(Owner).ConfigureAwait(false);
            LogActed(logger, Resume, ManualRequestDocument.SafeIdOrInvalid(id));
            return Remember(id, request, ("accepted", "Trading resumed. The risk limits still apply, and the automatic checks can halt it again."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailed(logger, ex.GetType().Name); // the engine state couldn't be saved: tried again at the next poll
            return null;
        }
    }

    /// <summary>Records what the request became; <paramref name="counts"/> toward <see cref="MaxActsPerDay"/> unless refused for it.</summary>
    private (string Status, string Detail) Remember(string id, ParsedRequest request, (string Status, string Detail) outcome, bool counts = true)
    {
        _outcomes[id] = outcome;
        if (_lastActedAt is null || request.CreatedAt > _lastActedAt)
            _lastActedAt = request.CreatedAt;
        if (counts)
            _actsToday++;
        return outcome;
    }

    /// <summary>Whether the halt in place is the one the page showed: the same start, and the same later cause or none.</summary>
    internal static bool SameHalt(DateTimeOffset since, DateTimeOffset? alsoAt, ParsedRequest request)
    {
        static bool Near(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= SameHaltTolerance;
        return request.HaltSince is { } seen && Near(since, seen)
            && (alsoAt is { } also ? request.HaltAlsoAt is { } seenAlso && Near(also, seenAlso) : request.HaltAlsoAt is null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                    _lastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    if (_lastError != ex.GetType().Name)
                        LogFailed(logger, ex.GetType().Name); // once per kind of failure, not every few seconds
                    _lastError = ex.GetType().Name;
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <param name="HaltSince">Resume: the start of the halt the page showed.</param>
    /// <param name="HaltAlsoAt">Resume: when the later cause the page showed was found, or null when it showed none.</param>
    internal sealed record ParsedRequest(
        string? Action, DateTimeOffset? CreatedAt, string? Problem, bool Expired = false, DateTimeOffset? HaltSince = null, DateTimeOffset? HaltAlsoAt = null);

    /// <summary>A dashboard request as Pause or Resume, or why it can't be one. The engine trusts nothing in the document.</summary>
    internal static ParsedRequest Parse(JsonObject document, DateTimeOffset now)
    {
        if (ManualRequestDocument.ShapeProblem(document) is { } shape)
            return new ParsedRequest(null, null, shape);
        if (ManualRequestDocument.SafeIdOrInvalid(ManualRequestDocument.Id(document)) == "invalid-id")
            return new ParsedRequest(null, null, "id must be 8 to 64 letters, digits or dashes.");
        var createdAt = Time(ManualRequestDocument.Field(document, "createdAt"));
        if (createdAt is null)
            return new ParsedRequest(null, null, "The request has no valid createdAt.");
        if (ManualRequestDocument.Day(document) != createdAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return new ParsedRequest(null, createdAt, "day must be the UTC date of createdAt.");
        if (createdAt - now > ClockSkew)
            return new ParsedRequest(null, createdAt, "The request's time is in the future.");
        var action = ManualRequestDocument.Field(document, "action");
        if (action is not (Pause or Resume))
            return new ParsedRequest(null, createdAt, "action must be pause or resume.");
        if (now - createdAt > RequestMaxAge)
            return new ParsedRequest(null, createdAt, "The request was more than 5 minutes old when the engine saw it; press the button again.", Expired: true);
        if (action == Pause)
            return new ParsedRequest(action, createdAt, null);

        var haltSince = Time(ManualRequestDocument.Field(document, "haltSince"));
        if (haltSince is null)
            return new ParsedRequest(null, createdAt, "A Resume request needs haltSince: the start of the halt the page showed.");
        var alsoText = ManualRequestDocument.Field(document, "haltAlsoAt");
        var haltAlsoAt = Time(alsoText);
        if (alsoText is not null && haltAlsoAt is null)
            return new ParsedRequest(null, createdAt, "haltAlsoAt must be a time.");
        return new ParsedRequest(action, createdAt, null, HaltSince: haltSince, HaltAlsoAt: haltAlsoAt);
    }

    private static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pause and Resume requests could not be read, acted on or updated ({Error}); retried at the next poll")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Owner {Action} request {Id} acted on")]
    private static partial void LogActed(ILogger logger, string action, string id);
}
