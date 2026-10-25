using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Analysis;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Strategy;

namespace Cix.Engine.Manual;

/// <summary>An agent-assisted request waiting for the trader, with when it started waiting.</summary>
public sealed record PendingRequest(ManualRequest Request, DateTimeOffset AwaitingSince, DateTimeOffset AnswerBy);

/// <summary>What happened to a request the engine acted on: a status from <see cref="ManualRequestStatus"/>, a rule code and a reason.</summary>
public sealed record ManualOutcome(
    string Status, string Rule, string Reason, string? ClientOrderId = null, decimal? Size = null, decimal? Price = null, string? OrderId = null);

/// <summary>
/// The owner's manual buy and sell requests (ADR-0011). Polls the <c>requests</c> container, checks each new request, and:
/// <list type="bullet">
/// <item>direct sells: sells the requested portion of the position at the bid (or the owner's limit) at once;</item>
/// <item>agent-assisted sells and buys: waits for the trader's answer (<see cref="AnswerAsync"/>), then places what it agreed to,
/// never more than the owner asked.</item>
/// </list>
/// Every order goes through <see cref="ExecutionService"/>, and so the risk gate. A request is claimed (status
/// <c>executing</c>, with its client order ID) before its order is sent, so a request is never executed twice, even across a
/// restart; the execution service refuses a reused client order ID as well. Every step is journaled as <c>manual-request</c>
/// and recorded on the request document, whose history the dashboard shows.
/// </summary>
public sealed partial class ManualRequestService(
    IManualRequestStore store,
    ExecutionService execution,
    MarketState market,
    RiskLimits limits,
    IJournal journal,
    IStrategySpecStore specs,
    ManualRequestOptions options,
    TradingContext trading,
    TimeProvider time,
    ILogger<ManualRequestService> logger,
    MarketViewStore? views = null) : BackgroundService
{
    /// <summary>
    /// The trading mode a request is for: its <c>tradingMode</c> field, <c>paper</c> when absent. An engine acts only on
    /// requests for its own mode, so a live engine never acts on a request that didn't say <c>live</c>.
    /// </summary>
    public static string ModeOf(JsonObject document) =>
        ManualRequestDocument.Field(document ?? throw new ArgumentNullException(nameof(document)), "tradingMode")?.ToLowerInvariant() ?? "paper";

    private string EngineMode => trading.Mode.ToString().ToLowerInvariant();

    public const string Kind = "manual-request";
    public const string DirectSource = "manual-direct";
    public const string AgentSource = "manual-agent";
    public const string EngineActor = "engine";

    /// <summary>How often orders placed for requests are checked for fills.</summary>
    public static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(30);

    /// <summary>A request claimed for execution this long ago, with no order known to the engine or the journal, was never sent.</summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far a request's timestamp may be ahead of the engine's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, JsonObject> _unsaved = new(StringComparer.Ordinal);
    private IReadOnlyList<PendingRequest> _pending = [];
    private DateTimeOffset? _lastSettle;
    private string? _lastError;

    /// <summary><c>ok</c>, <c>off</c> (disabled in configuration), <c>unavailable</c> (no requests container) or <c>not-used</c> (no database).</summary>
    public string State => options.Enabled ? store.State : "off";

    public DateTimeOffset? LastPollAt { get; private set; }

    /// <summary>Agent-assisted requests waiting for the trader, as of the last poll.</summary>
    public IReadOnlyList<PendingRequest> Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// The waiting requests as a stable key (<c>manual:&lt;id&gt;|...</c>), empty when none. The trader loop starts a run when
    /// it holds an item it hasn't handled.
    /// </summary>
    public string AgentKey => string.Join('|', Pending.Select(p => "manual:" + p.Request.Id).Order(StringComparer.Ordinal));

    /// <summary>One pass: new requests, expired waits, then fills of orders placed for requests. Returns how many requests changed.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (State != "ok")
            return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RetryUnsavedAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var active = (await store.ListActiveAsync(today.AddDays(-1), cancellationToken).ConfigureAwait(false))
                .Where(r => ModeOf(r.Document) == EngineMode).ToList();
            var changed = 0;

            var inFlight = active.Where(r => ManualRequestStatus.InFlight.Contains(r.Status)).Select(r => KeyOf(r.Document)).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var awaiting = active.Where(r => r.Status == ManualRequestStatus.AwaitingAgent).ToList();
            int? acceptedToday = null;

            foreach (var item in active.Where(r => r.Status == ManualRequestStatus.New)
                         .OrderBy(r => ManualRequestDocument.Field(r.Document, "createdAt"), StringComparer.Ordinal)
                         .ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                acceptedToday ??= await AcceptedOnAsync(today, cancellationToken).ConfigureAwait(false);
                var (next, accepted) = await HandleNewAsync(item, inFlight, acceptedToday.Value, now, cancellationToken).ConfigureAwait(false);
                changed++;
                if (accepted)
                    acceptedToday++;
                if (next?.Status == ManualRequestStatus.AwaitingAgent)
                    awaiting.Add(next);
            }

            var pending = new List<PendingRequest>();
            foreach (var item in awaiting)
            {
                var (request, _) = ManualRequestDocument.Parse(item.Document, limits.AllowedSymbols);
                var since = ManualRequestDocument.LastAt(item.Document, ManualRequestStatus.AwaitingAgent) ?? request?.CreatedAt ?? now;
                if (request is null || now - since >= options.AgentAnswerTimeout)
                {
                    await FinishAsync(item, ManualRequestStatus.Expired, EngineActor, "no-answer",
                        Inv($"The trader didn't answer within {options.AgentAnswerTimeout.TotalMinutes:0} minutes."), null, cancellationToken).ConfigureAwait(false);
                    changed++;
                }
                else
                {
                    pending.Add(new PendingRequest(request, since, since + options.AgentAnswerTimeout));
                }
            }

            Volatile.Write(ref _pending, pending.OrderBy(p => p.AwaitingSince).ToList());

            if (_lastSettle is null || now - _lastSettle >= SettleInterval)
            {
                changed += await SettleAsync(active.Where(r => r.Status is ManualRequestStatus.Executing or ManualRequestStatus.Placed or ManualRequestStatus.Unconfirmed), now, cancellationToken)
                    .ConfigureAwait(false);
                _lastSettle = now;
            }

            LastPollAt = now;
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The trader's answer to an agent-assisted request: <c>execute</c> (the whole request, or for a sell a smaller
    /// <paramref name="portion"/>) or <c>decline</c>. A buy needs a stop-loss, which becomes the position's plan.
    /// </summary>
    public async Task<ManualOutcome> AnswerAsync(
        string requestId, string decision, decimal? portion, decimal? stopLoss, decimal? takeProfit, string rationale, string agent, string? runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (State != "ok")
            return new ManualOutcome("unavailable", "manual-requests-off", "Manual requests are not available right now.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var waiting = Pending.FirstOrDefault(p => p.Request.Id == requestId);
            var item = waiting is null ? null : await store.GetAsync(requestId, waiting.Request.Day, cancellationToken).ConfigureAwait(false);
            if (item is null || item.Status != ManualRequestStatus.AwaitingAgent || ModeOf(item.Document) != EngineMode)
                return new ManualOutcome("not-pending", "not-pending", "No request with that ID is waiting for an answer; it may have expired or been answered.");
            var (request, problem) = ManualRequestDocument.Parse(item.Document, limits.AllowedSymbols);
            if (request is null)
                return new ManualOutcome("not-pending", "invalid", problem ?? "The request is not valid.");

            ManualOutcome outcome;
            if (decision == "decline")
            {
                await FinishAsync(item, ManualRequestStatus.Declined, agent, "declined", rationale,
                    new Dictionary<string, string?> { ["decidedBy"] = agent, ["runId"] = runId, ["rationale"] = rationale }, cancellationToken,
                    runId).ConfigureAwait(false);
                outcome = new ManualOutcome(ManualRequestStatus.Declined, "declined", "Recorded: the request is declined.");
            }
            else
            {
                var share = request.IsSell ? portion ?? request.Portion : portion;
                if (market.Quote(request.Symbol) is not { } quote)
                    return new ManualOutcome("invalid-answer", "no-quote", $"No current price for {request.Symbol}; try again shortly or decline.");
                if (AnswerProblem(request, share, stopLoss, takeProfit, quote) is { } invalid)
                    return new ManualOutcome("invalid-answer", "invalid", invalid); // the request still waits: the trader can answer again

                outcome = await PlaceForRequestAsync(item, request, share, stopLoss, takeProfit, AgentSource, agent, runId, rationale, cancellationToken)
                    .ConfigureAwait(false);
            }

            Volatile.Write(ref _pending, Pending.Where(p => p.Request.Id != requestId).ToList());
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Requests of <paramref name="day"/> the engine accepted (whatever came of them); the daily cap counts these.</summary>
    private async Task<int> AcceptedOnAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var all = (await store.ListDayAsync(day, cancellationToken).ConfigureAwait(false)).Where(r => ModeOf(r.Document) == EngineMode);
        return all.Count(r => r.Status is not (ManualRequestStatus.New or ManualRequestStatus.Invalid or ManualRequestStatus.Duplicate or ManualRequestStatus.Expired)
            && !(r.Status == ManualRequestStatus.Rejected && ManualRequestDocument.Outcome(r.Document, "rule") == "daily-cap"));
    }

    private async Task<(StoredRequest? Next, bool Accepted)> HandleNewAsync(
        StoredRequest item, HashSet<string> inFlight, int acceptedToday, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (request, problem) = ManualRequestDocument.Parse(item.Document, limits.AllowedSymbols);
        if (request is null)
            return (await FinishAsync(item, ManualRequestStatus.Invalid, EngineActor, "invalid", problem!, null, cancellationToken).ConfigureAwait(false), false);
        if (request.CreatedAt > now + ClockSkew)
            return (await FinishAsync(item, ManualRequestStatus.Invalid, EngineActor, "invalid", "createdAt is in the future.", null, cancellationToken).ConfigureAwait(false), false);
        if (now - request.CreatedAt > options.MaxAgeAtPickup)
        {
            var reason = Inv($"The request was {(now - request.CreatedAt).TotalMinutes:0} minutes old when the engine saw it (at most {options.MaxAgeAtPickup.TotalMinutes:0}); not acted on. Send it again if it still applies.");
            return (await FinishAsync(item, ManualRequestStatus.Expired, EngineActor, "stale", reason, null, cancellationToken).ConfigureAwait(false), false);
        }

        if (acceptedToday >= options.MaxPerDay)
        {
            var reason = Inv($"At most {options.MaxPerDay} manual requests a day (ManualRequests:MaxPerDay).");
            return (await FinishAsync(item, ManualRequestStatus.Rejected, EngineActor, "daily-cap", reason, null, cancellationToken).ConfigureAwait(false), false);
        }

        var key = Key(request.Symbol, request.Side);
        if (inFlight.Contains(key))
        {
            var reason = $"Another {request.Side} request for {request.Symbol} is still in progress.";
            return (await FinishAsync(item, ManualRequestStatus.Duplicate, EngineActor, "duplicate", reason, null, cancellationToken).ConfigureAwait(false), false);
        }

        inFlight.Add(key);
        await JournalAsync("owner", null, Inv($"Owner request {request.Id}: {request.Describe()}{(request.Note is null ? string.Empty : $" ({request.Note})")}"),
            Data(request, "received", "received", "Received from " + request.RequestedBy + ".")).ConfigureAwait(false);

        if (request.Mode == ManualRequest.Agent)
        {
            var next = await FinishAsync(item, ManualRequestStatus.AwaitingAgent, EngineActor, "sent-to-trader", "Sent to the trader to analyse.", null, cancellationToken)
                .ConfigureAwait(false);
            return (next, true);
        }

        await PlaceForRequestAsync(item, request, request.Portion, null, null, DirectSource, EngineActor, null,
            request.Note is null ? "Owner's direct sell." : $"Owner's direct sell: {request.Note}", cancellationToken).ConfigureAwait(false);
        return (null, true);
    }

    /// <summary>Prices and sizes the order, claims the request, sends the order through the execution service and records the outcome.</summary>
    private async Task<ManualOutcome> PlaceForRequestAsync(
        StoredRequest item, ManualRequest request, decimal? portion, decimal? stopLoss, decimal? takeProfit, string source, string decidedBy,
        string? runId, string rationale, CancellationToken cancellationToken)
    {
        var extra = new Dictionary<string, string?> { ["decidedBy"] = decidedBy, ["runId"] = runId, ["rationale"] = rationale, ["source"] = source };
        async Task<ManualOutcome> Refuse(string rule, string reason)
        {
            await FinishAsync(item, ManualRequestStatus.Rejected, decidedBy, rule, reason, extra, cancellationToken, runId).ConfigureAwait(false);
            return new ManualOutcome(ManualRequestStatus.Rejected, rule, reason);
        }

        if (market.Quote(request.Symbol) is not { } quote)
            return await Refuse("no-quote", $"No current price for {request.Symbol}.").ConfigureAwait(false);

        if (AnswerProblem(request, portion, stopLoss, takeProfit, quote) is { } problem)
            return await Refuse("invalid", problem).ConfigureAwait(false);

        decimal size, price;
        if (request.IsSell)
        {
            var share = portion!.Value;
            var valued = await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false);
            if (valued is not { } portfolio)
                return await Refuse("portfolio-unavailable", "The portfolio can't be valued right now.").ConfigureAwait(false);
            var sellable = portfolio.Snapshot.SellableBaseBySymbol?.GetValueOrDefault(request.Symbol) ?? 0m;
            if (sellable <= 0m)
                return await Refuse("nothing-to-sell", $"There is no sellable {request.Symbol} position (an open sell order may hold it).").ConfigureAwait(false);
            size = RoundDown(sellable * share);
            price = request.LimitPrice ?? quote.Bid;
            extra["portion"] = share.ToString(Invariant);
        }
        else
        {
            price = quote.Ask;
            if (views?.Current is { } view)
            {
                var now = time.GetUtcNow();
                if (view.BlocksEntries(now))
                    return await Refuse("market-view", Inv($"New buys are paused by the market view (risk-off) until {view.ValidUntil:yyyy-MM-dd HH:mm} UTC.")).ConfigureAwait(false);
                if (view.FlagFor(request.Symbol, now) is { } flag)
                    return await Refuse("market-view", Inv($"{request.Symbol} is on the market view's avoid list until {flag.Until:yyyy-MM-dd HH:mm} UTC.")).ConfigureAwait(false);
            }

            size = RoundDown(request.QuoteAmount!.Value / price);
            if (stopLoss is { } sl)
                extra["stopLoss"] = sl.ToString(Invariant);
            if (takeProfit is { } tp)
                extra["takeProfit"] = tp.ToString(Invariant);
        }

        if (size <= 0m)
            return await Refuse("too-small", "The resulting order size is zero.").ConfigureAwait(false);

        // Claim first, with the client order ID: if anything stops between here and the outcome, the request is never sent twice.
        var clientOrderId = Guid.NewGuid().ToString();
        extra["clientOrderId"] = clientOrderId;
        extra["side"] = request.Side;
        extra["requestedSize"] = size.ToString(Invariant);
        extra["requestedPrice"] = price.ToString(Invariant);
        var claim = await store.TryReplaceAsync(
            item,
            ManualRequestDocument.WithStatus(item.Document, ManualRequestStatus.Executing, time.GetUtcNow(), decidedBy,
                Inv($"Sending {request.Side} {size} {request.Symbol} at {price}."), extra),
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
            return new ManualOutcome("changed", "changed", "The request changed while it was being handled; nothing was sent.");

        var side = request.IsSell ? OrderSide.Sell : OrderSide.Buy;
        var result = await execution.PlaceAsync(
            new OrderIntent(request.Symbol, side, size, price, clientOrderId), postOnly: false, cancellationToken,
            countsTowardRejectStreak: !request.IsSell, // a sell never adds risk, as for the engine's protective sells
            context: new OrderContext(source, stopLoss, takeProfit, runId, specs.Active?.Label)).ConfigureAwait(false);

        var status = result.Status switch
        {
            ExecutionStatus.Placed => ManualRequestStatus.Placed,
            ExecutionStatus.Uncertain => ManualRequestStatus.Unconfirmed,
            ExecutionStatus.Rejected => ManualRequestStatus.Rejected,
            _ => ManualRequestStatus.Failed,
        };
        var orderData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = source,
            ["requestId"] = request.Id,
            ["clientOrderId"] = clientOrderId,
            ["symbol"] = request.Symbol,
            ["side"] = request.Side,
            ["requestedSize"] = size.ToString(Invariant),
            ["requestedPrice"] = price.ToString(Invariant),
            ["status"] = result.Status.ToString().ToLowerInvariant(),
            ["rule"] = result.Rule,
            ["reason"] = result.Reason,
            ["rationale"] = rationale,
        };
        extra["rule"] = result.Rule;
        extra["reason"] = result.Reason;
        if (stopLoss is { } s)
            orderData["stopLoss"] = s.ToString(Invariant);
        if (takeProfit is { } t)
            orderData["takeProfit"] = t.ToString(Invariant);
        if (result.Order is { } approved)
        {
            orderData["approvedSize"] = approved.BaseSize.ToString(Invariant);
            orderData["approvedPrice"] = approved.LimitPrice.ToString(Invariant);
            extra["approvedSize"] = orderData["approvedSize"];
            extra["approvedPrice"] = orderData["approvedPrice"];
        }

        if (result.Placed is { } placed)
        {
            orderData["orderId"] = placed.VenueOrderId;
            extra["orderId"] = placed.VenueOrderId;
        }

        var actor = decidedBy == EngineActor ? EngineActor : decidedBy;
        await AppendAsync(new JournalEntry(
            JournalIds.New(time.GetUtcNow(), "order"), time.GetUtcNow(), "order", actor, runId, specs.Active?.Label,
            Inv($"manual {request.Side} {size} {request.Symbol} @ {price}: {result.Status} ({result.Rule})"), orderData)).ConfigureAwait(false);

        // The order exists (or may): record the outcome whatever the caller's token says, and keep retrying until it is saved.
        await FinishAsync(claim, status, decidedBy, result.Rule, result.Reason, extra, CancellationToken.None, runId).ConfigureAwait(false);
        return new ManualOutcome(status, result.Rule, result.Reason, clientOrderId, result.Order?.BaseSize ?? size, result.Order?.LimitPrice ?? price,
            result.Placed?.VenueOrderId);
    }

    /// <summary>
    /// Why the trader's answer can't be carried out as given, or null: a sell's portion must be above 0 and at most what the
    /// owner asked, and takes no stop or target; a buy takes no portion and needs a stop (and any target) a real distance
    /// from the ask, as place_order does.
    /// </summary>
    private static string? AnswerProblem(ManualRequest request, decimal? portion, decimal? stopLoss, decimal? takeProfit, MarketQuote quote)
    {
        if (request.IsSell)
        {
            if (portion is not { } share || share <= 0m || share > (request.Portion ?? 0m))
                return Inv($"portion must be more than 0 and at most what the owner asked ({request.Portion}).");
            return stopLoss is not null || takeProfit is not null ? "stopLoss and takeProfit apply to buys only." : null;
        }

        if (portion is not null)
            return "A buy takes no portion: the owner set the amount.";
        var ask = quote.Ask;
        var distance = Cix.Domain.Scanning.MarketScanner.MinExitDistanceFraction;
        if (stopLoss is not { } stop || stop <= 0m || stop > ask * (1 - distance))
            return Inv($"A buy needs a stopLoss at least {distance:P1} below the ask ({ask}).");
        if (takeProfit is { } target && target < ask * (1 + distance))
            return Inv($"takeProfit must be at least {distance:P1} above the ask ({ask}).");
        return null;
    }

    /// <summary>Moves orders placed for requests to filled or closed once the journal has their outcome; resolves claims a restart interrupted.</summary>
    private async Task<int> SettleAsync(IEnumerable<StoredRequest> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var item in items)
        {
            if (_unsaved.ContainsKey(item.Id))
                continue; // its outcome is known here and still being saved
            var clientOrderId = ManualRequestDocument.Outcome(item.Document, "clientOrderId");
            if (clientOrderId is null || !DateOnly.TryParseExact(item.Day, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var day))
                continue;

            var entries = await journal.QueryAsync(new JournalQuery(From: day, ClientOrderId: clientOrderId, Limit: 50), cancellationToken).ConfigureAwait(false);
            if (entries.FirstOrDefault(e => e.Kind is "fill" or "order-closed") is { } end)
            {
                var filled = end.Kind == "fill";
                var outcome = new Dictionary<string, string?>
                {
                    ["filledSize"] = end.Data?.GetValueOrDefault("filledSize"),
                    ["averagePrice"] = end.Data?.GetValueOrDefault("averagePrice"),
                    ["orderStatus"] = end.Data?.GetValueOrDefault("status"),
                };
                await FinishAsync(item, filled ? ManualRequestStatus.Filled : ManualRequestStatus.Closed, EngineActor, end.Kind,
                    filled ? $"Filled {end.Data?.GetValueOrDefault("filledSize")} at {end.Data?.GetValueOrDefault("averagePrice")}." : "The order ended without a fill.",
                    outcome, cancellationToken).ConfigureAwait(false);
                changed++;
                continue;
            }

            if (item.Status != ManualRequestStatus.Executing)
                continue; // placed or unconfirmed: the reconciler journals how it ends

            // Claimed, then the engine stopped before recording the outcome.
            var claimedAt = ManualRequestDocument.LastAt(item.Document, ManualRequestStatus.Executing) ?? now;
            var order = entries.FirstOrDefault(e => e.Kind == "order");
            if (execution.IsWorkingOrder(clientOrderId) || order?.Data?.GetValueOrDefault("status") is "placed" or "uncertain")
            {
                await FinishAsync(item, ManualRequestStatus.Unconfirmed, EngineActor, "restarted",
                    "The engine restarted while handling this request; the order is being reconciled.", null, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            else if (order?.Data?.GetValueOrDefault("status") is "rejected" or "failed")
            {
                await FinishAsync(item, order.Data!["status"] == "rejected" ? ManualRequestStatus.Rejected : ManualRequestStatus.Failed, EngineActor,
                    order.Data.GetValueOrDefault("rule") ?? "unknown", order.Data.GetValueOrDefault("reason") ?? "See the journal.", null, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            else if (now - claimedAt >= ClaimGrace)
            {
                await FinishAsync(item, ManualRequestStatus.Closed, EngineActor, "not-sent",
                    "The engine stopped before sending the order; nothing was sent. Send the request again if it still applies.", null, cancellationToken).ConfigureAwait(false);
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Sets the request's status, records the step in its history and the journal. Returns the new version, or null if it changed meanwhile.</summary>
    private async Task<StoredRequest?> FinishAsync(
        StoredRequest item, string status, string by, string rule, string reason, IReadOnlyDictionary<string, string?>? extra,
        CancellationToken cancellationToken, string? runId = null)
    {
        var outcome = new Dictionary<string, string?>(extra ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
        {
            ["rule"] = rule,
            ["reason"] = ManualRequestDocument.Clean(reason, 1_000),
        };
        var next = ManualRequestDocument.WithStatus(item.Document, status, time.GetUtcNow(), by, reason, outcome);
        StoredRequest? saved;
        try
        {
            saved = await store.TryReplaceAsync(item, next, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (item.Status != ManualRequestStatus.Executing)
                throw;
            _unsaved[item.Id] = next; // an order was sent: its outcome must reach the request, so it is retried at every poll
            LogSaveFailed(logger, item.Id, ex.GetType().Name);
            saved = null;
        }

        if (saved is null && !_unsaved.ContainsKey(item.Id))
            return null; // changed by someone else: the next poll reads it again

        var (request, _) = ManualRequestDocument.Parse(item.Document, limits.AllowedSymbols);
        var data = request is null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["requestId"] = item.Id, ["status"] = status, ["rule"] = rule, ["reason"] = outcome["reason"] ?? string.Empty }
            : Data(request, status, rule, outcome["reason"] ?? string.Empty);
        foreach (var key in new[] { "clientOrderId", "rationale", "runId", "portion", "filledSize", "averagePrice" })
        {
            if (outcome.GetValueOrDefault(key) is { Length: > 0 } value)
                data[key] = value;
        }

        var subject = request is null ? $"Owner request {item.Id}" : $"Owner request {item.Id} ({request.Describe()})";
        await JournalAsync(by == EngineActor ? EngineActor : by, runId, $"{subject}: {status} ({rule})", data).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Outcomes that couldn't be saved after an order was sent: saved now, onto the latest version of the request.</summary>
    private async Task RetryUnsavedAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, final) in _unsaved.ToList())
        {
            var day = ManualRequestDocument.Day(final) ?? string.Empty;
            var current = await store.GetAsync(id, day, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Status != ManualRequestStatus.Executing
                || await store.TryReplaceAsync(current, final, cancellationToken).ConfigureAwait(false) is not null)
                _unsaved.Remove(id);
        }
    }

    private Task JournalAsync(string actor, string? runId, string summary, Dictionary<string, string> data) =>
        AppendAsync(new JournalEntry(JournalIds.New(time.GetUtcNow(), Kind), time.GetUtcNow(), Kind, actor, runId, specs.Active?.Label, summary, data));

    private async Task AppendAsync(JournalEntry entry)
    {
        try
        {
            await journal.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalFailed(logger, entry.Kind, ex.GetType().Name);
        }
    }

    private static Dictionary<string, string> Data(ManualRequest request, string status, string rule, string reason)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requestId"] = request.Id,
            ["mode"] = request.Mode,
            ["side"] = request.Side,
            ["symbol"] = request.Symbol,
            ["status"] = status,
            ["rule"] = rule,
            ["reason"] = reason,
            ["requestedBy"] = request.RequestedBy,
        };
        if (request.Portion is { } portion)
            data["requestedPortion"] = portion.ToString(Invariant);
        if (request.QuoteAmount is { } amount)
            data["quoteAmount"] = amount.ToString(Invariant);
        if (request.LimitPrice is { } limit)
            data["limitPrice"] = limit.ToString(Invariant);
        if (request.Note is { } note)
            data["note"] = note;
        return data;
    }

    private static string Key(string symbol, string side) => $"{side}:{symbol}";

    private static string? KeyOf(JsonObject document)
    {
        var symbol = ManualRequestDocument.Field(document, "symbol");
        var side = ManualRequestDocument.Field(document, "side")?.ToLowerInvariant();
        return symbol is null || side is null ? null : Key(MarketDataPoller.NormaliseSymbol(symbol), side);
    }

    private static decimal RoundDown(decimal value) => Math.Floor(value * 100_000_000m) / 100_000_000m;

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (State != "ok")
        {
            if (State == "unavailable")
                LogNotRunning(logger);
            return; // decided at startup: the container appears only after provisioning and a restart
        }

        using var timer = new PeriodicTimer(options.PollInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                    _lastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    if (_lastError != ex.GetType().Name)
                        LogPollFailed(logger, ex.GetType().Name, ex.Message); // once per kind of failure, not every few seconds
                    _lastError = ex.GetType().Name;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual requests are off: the database has no requests container. Run tools/azure/provision.ps1 again, then restart the engine")]
    private static partial void LogNotRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual requests poll failed ({Error}): {Detail}")]
    private static partial void LogPollFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Manual request {Id}: the outcome of its order could not be saved ({Error}); retrying")]
    private static partial void LogSaveFailed(ILogger logger, string id, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for a {Kind} entry ({Error})")]
    private static partial void LogJournalFailed(ILogger logger, string kind, string error);
}

public static class ManualRequestRegistration
{
    /// <summary>The service and its poll loop, over an in-memory store until Cosmos DB replaces it (AddCixCosmos).</summary>
    public static IServiceCollection AddCixManualRequests(this IServiceCollection services, ManualRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IManualRequestStore>(new InMemoryManualRequestStore());
        services.AddSingleton<ManualRequestService>();
        services.AddHostedService(sp => sp.GetRequiredService<ManualRequestService>());
        return services;
    }
}
