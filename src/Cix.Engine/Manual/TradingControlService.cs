using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Engine.Execution;
using Cix.Engine.Persistence;

namespace Cix.Engine.Manual;

/// <summary>
/// The owner's Pause and Resume buttons on the dashboard (ADR-0018), in either trading mode: <see cref="RequestType"/>
/// documents in the <c>requests</c> container, which the dashboard can only create. The newest valid request acts and older
/// ones are superseded. Pause engages the kill switch as the owner; Resume releases it, as the operator tool
/// <c>resume_trading</c> does, but only a halt that was already in place when the owner pressed it: a halt that engaged
/// after the request's <c>createdAt</c> is refused, so the owner never releases one they haven't seen. The execution
/// service journals each change (kind <c>kill-switch</c>); the request's document records what became of it.
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

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>A request older than this when the engine first sees it expires instead of acting on a stale intent.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How far a request's timestamp may be ahead of the engine's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>Requests acted on in this run, so one whose document couldn't be updated isn't acted on again.</summary>
    private readonly HashSet<string> _acted = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the new requests for this engine's mode: acts on the newest valid one and records the outcome on each. Returns
    /// how many documents it handled. Tests call this instead of running the loop.
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
        var newest = parsed.LastOrDefault(p => p.Parsed.Action is not null).Stored;

        // The newest first: older ones are superseded only once it has acted, so a failure drops none of them.
        var handled = 0;
        foreach (var (stored, request) in parsed.OrderBy(p => ReferenceEquals(p.Stored, newest) ? 0 : 1))
        {
            string status, detail;
            if (request.Action is null)
                (status, detail) = (request.Expired ? ManualRequestStatus.Expired : ManualRequestStatus.Invalid, request.Problem!);
            else if (!ReferenceEquals(stored, newest))
                (status, detail) = ("superseded", "A newer Pause or Resume request replaced this one.");
            else if (await ActAsync(stored.Id, request, cancellationToken).ConfigureAwait(false) is { } outcome)
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
    private async Task<(string Status, string Detail)?> ActAsync(string id, ParsedRequest request, CancellationToken cancellationToken)
    {
        if (_acted.Contains(id))
            return ("accepted", "Done.");
        var state = killSwitch.State;
        try
        {
            if (request.Action == Pause)
            {
                if (state.Engaged)
                {
                    _acted.Add(id);
                    return ("accepted", "Trading was already halted; the halt and its reason stay as they were.");
                }

                var reason = request.Note is { } note ? $"Paused by the owner: {note}" : "Paused by the owner from the dashboard.";
                // Not cancellable once started: the switch must never end up engaged with orders left uncancelled.
                var result = await execution.EngageKillSwitchAsync(reason, Owner, CancellationToken.None).ConfigureAwait(false);
                _acted.Add(id);
                LogActed(logger, Pause, id);
                return ("accepted", result.NotCancelled.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Trading halted; {result.Cancelled} open order(s) cancelled.")
                    : string.Create(CultureInfo.InvariantCulture, $"Trading halted; {result.Cancelled} open order(s) cancelled, {result.NotCancelled.Count} couldn't be: check the exchange."));
            }

            if (!state.Engaged)
            {
                _acted.Add(id);
                return ("accepted", "Trading wasn't halted; nothing to resume.");
            }

            if (state.Since is { } since && since > request.CreatedAt)
            {
                // Not the halt the owner saw: one that engaged after they pressed Resume stays until they've seen it.
                _acted.Add(id);
                return (ManualRequestStatus.Rejected,
                    $"Trading halted again after you pressed Resume: {Status.EngineStatusService.HaltCategoryFor(state.By)} Check why on the dashboard, then press Resume again if you still want to.");
            }

            await execution.ReleaseKillSwitchAsync(Owner).ConfigureAwait(false);
            _acted.Add(id);
            LogActed(logger, Resume, id);
            return ("accepted", "Trading resumed. The risk limits still apply, and the automatic checks can halt it again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailed(logger, ex.GetType().Name); // the engine state couldn't be saved: tried again at the next poll
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal sealed record ParsedRequest(string? Action, DateTimeOffset? CreatedAt, string? Note, string? Problem, bool Expired = false);

    /// <summary>A dashboard request as Pause or Resume, or why it can't be one. The engine trusts nothing in the document.</summary>
    internal static ParsedRequest Parse(JsonObject document, DateTimeOffset now)
    {
        if (ManualRequestDocument.ShapeProblem(document) is { } shape)
            return new ParsedRequest(null, null, null, shape);
        var createdAt = ManualRequestDocument.Field(document, "createdAt") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        if (createdAt is null)
            return new ParsedRequest(null, null, null, "The request has no valid createdAt.");
        if (ManualRequestDocument.Day(document) != createdAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return new ParsedRequest(null, createdAt, null, "day must be the UTC date of createdAt.");
        if (createdAt - now > ClockSkew)
            return new ParsedRequest(null, createdAt, null, "The request's time is in the future.");
        var action = ManualRequestDocument.Field(document, "action");
        if (action is not (Pause or Resume))
            return new ParsedRequest(null, createdAt, null, "action must be pause or resume.");
        if (now - createdAt > RequestMaxAge)
            return new ParsedRequest(null, createdAt, null, "The request was more than 5 minutes old when the engine saw it; press the button again.", Expired: true);
        return new ParsedRequest(action, createdAt, ManualRequestDocument.Clean(ManualRequestDocument.Field(document, "note"), ManualRequest.MaxNote), null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pause and Resume requests could not be read, acted on or updated ({Error}); retried at the next poll")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Owner {Action} request {Id} acted on")]
    private static partial void LogActed(ILogger logger, string action, string id);
}
