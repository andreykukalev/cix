using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Engine.Manual;
using Cix.Engine.Persistence;

namespace Cix.Engine.AgentRuns;

/// <summary>
/// The owner's Run now button on the dashboard: <see cref="RequestType"/> documents in the <c>requests</c> container, which
/// the dashboard can only create, for this engine's mode. Each new request asks <see cref="AgentRunScheduler"/> for an
/// analyst run outside the schedule; the run rules decide (one at a time, a cooldown, a daily cap), and the request's
/// document records the answer. The request carries nothing the analyst reads: its run gets the same tools and prompt as a
/// scheduled one, so the button can only make the analyst look sooner.
/// </summary>
public sealed partial class AnalystRunRequestService(
    IManualRequestStore requests,
    TradingContext trading,
    TimeProvider time,
    ILogger<AnalystRunRequestService> logger,
    AgentRunScheduler? scheduler = null,
    IEngineLease? lease = null) : BackgroundService
{
    /// <summary>The <c>type</c> of a Run now request.</summary>
    public const string RequestType = "analyst-run";

    public const string Actor = "engine";

    /// <summary>Documents handled in one poll; the rest wait for the next.</summary>
    public const int MaxPerPoll = 10;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>A request older than this when the engine first sees it expires instead of acting on a stale intent.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How far a request's timestamp may be ahead of the engine's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private const string Accepted = "accepted";

    /// <summary>What each request answered in this run became, so one whose document couldn't be updated keeps its outcome.</summary>
    private readonly Dictionary<string, (string Status, string Detail)> _outcomes = new(StringComparer.Ordinal);

    private string? _lastError;

    /// <summary>
    /// Takes the new requests for this engine's mode, oldest first, and records the answer on each, at most
    /// <see cref="MaxPerPoll"/> a poll. Returns how many documents it handled. Tests call this instead of running the loop.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (lease is { IsHeld: false } || requests.State != "ok")
            return 0; // only the engine that trades answers; without the container there is nothing to read
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

        if (_outcomes.Count > 4 * MaxPerPoll) // answered and recorded: only those still listed as new can come back
            foreach (var id in _outcomes.Keys.Where(id => found.All(r => r.Id != id)).ToList())
                _outcomes.Remove(id);

        var handled = 0;
        var ordered = found.Select(r => (Stored: r, Parsed: Parse(r.Document, now)))
            .OrderBy(p => p.Parsed.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(p => p.Stored.Id, StringComparer.Ordinal).Take(MaxPerPoll);
        foreach (var (stored, request) in ordered)
        {
            string status, detail;
            if (_outcomes.TryGetValue(stored.Id, out var done))
                (status, detail) = done; // answered already: its own outcome, even once it would have expired
            else if (request.Problem is { } problem)
                (status, detail) = (request.Expired ? ManualRequestStatus.Expired : ManualRequestStatus.Invalid, problem);
            else if (await AskAsync(cancellationToken).ConfigureAwait(false) is { } answer)
            {
                _outcomes[stored.Id] = answer;
                (status, detail) = answer;
            }
            else
                return handled; // this engine can't decide now: everything stays new and is taken at the next poll

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
                LogFailed(logger, ex.GetType().Name); // read again next time; one already answered isn't asked again
            }

            handled++;
        }

        return handled;
    }

    /// <summary>The scheduler's answer as a status and detail, or null when it can't decide now.</summary>
    private async Task<(string Status, string Detail)?> AskAsync(CancellationToken cancellationToken)
    {
        if (scheduler is null)
        {
            return (ManualRequestStatus.Rejected,
                "This engine doesn't queue the agents' runs (AgentRuns:Mode Off): the analyst runs on its own loop in Docker Compose.");
        }

        var (answer, detail) = await scheduler.RunAnalystNowAsync(cancellationToken).ConfigureAwait(false);
        if (answer == AgentRunAnswer.Unavailable)
            return null;
        LogAnswered(logger, answer == AgentRunAnswer.Yes ? Accepted : ManualRequestStatus.Rejected);
        return (answer == AgentRunAnswer.Yes ? Accepted : ManualRequestStatus.Rejected, detail);
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

    internal sealed record ParsedRequest(DateTimeOffset? CreatedAt, string? Problem, bool Expired = false);

    /// <summary>A dashboard request as Run now, or why it can't be one. The engine trusts nothing in the document.</summary>
    internal static ParsedRequest Parse(JsonObject document, DateTimeOffset now)
    {
        if (ManualRequestDocument.ShapeProblem(document) is { } shape)
            return new ParsedRequest(null, shape);
        if (ManualRequestDocument.SafeIdOrInvalid(ManualRequestDocument.Id(document)) == "invalid-id")
            return new ParsedRequest(null, "id must be 8 to 64 letters, digits or dashes.");
        var text = ManualRequestDocument.Field(document, "createdAt");
        if (text is null || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
            return new ParsedRequest(null, "The request has no valid createdAt.");
        if (ManualRequestDocument.Day(document) != createdAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return new ParsedRequest(createdAt, "day must be the UTC date of createdAt.");
        if (createdAt - now > ClockSkew)
            return new ParsedRequest(createdAt, "The request's time is in the future.");
        if (now - createdAt > RequestMaxAge)
            return new ParsedRequest(createdAt, "The request was more than 5 minutes old when the engine saw it; press the button again.", Expired: true);
        return new ParsedRequest(createdAt, null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analyst Run now requests could not be read or updated ({Error}); retried at the next poll")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Owner's analyst Run now request answered: {Status}")]
    private static partial void LogAnswered(ILogger logger, string status);
}
