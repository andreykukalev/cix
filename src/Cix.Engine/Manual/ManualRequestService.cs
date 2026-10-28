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
/// The owner's manual buy and sell requests (ADR-0011). Polls the <c>requests</c> container for new requests, checks each, and:
/// <list type="bullet">
/// <item>direct sells: sells the requested portion of the position at the bid at once;</item>
/// <item>agent-assisted sells and buys: waits for the trader's answer (<see cref="AnswerAsync"/>), then places what it agreed
/// to, never more than the owner asked.</item>
/// </list>
/// Every order goes through <see cref="ExecutionService"/>, and so the risk gate. What the engine knows about a request after
/// <c>new</c> lives in its own <see cref="ManualLedger"/>, saved before anything is sent: a request is claimed there, with its
/// client order ID, before its order goes out, so it is never executed twice, even across a restart. The request document
/// gets a copy of each step for people to read; the engine never trusts it back. Every step is journaled as
/// <c>manual-request</c>.
/// </summary>
public sealed partial class ManualRequestService(
    IManualRequestStore store,
    IManualLedgerStore ledgerStore,
    ExecutionService execution,
    MarketState market,
    IJournal journal,
    IStrategySpecStore specs,
    ManualRequestOptions options,
    TradingContext trading,
    TimeProvider time,
    ILogger<ManualRequestService> logger,
    MarketViewStore? views = null,
    TradableSymbols? tradable = null) : BackgroundService
{
    public const string Kind = "manual-request";
    public const string DirectSource = "manual-direct";
    public const string AgentSource = "manual-agent";
    public const string EngineActor = "engine";

    /// <summary>The tool a spec must allow for the trader to act on requests (the manual tools come with it).</summary>
    public const string TraderTool = "place_order";

    /// <summary>New requests handled per poll; more wait for the next poll.</summary>
    public const int MaxPerPoll = 10;

    /// <summary>Requests offered to the trader at once.</summary>
    public const int MaxPending = 10;

    /// <summary>Refusals journaled one by one per day; beyond this they are only recorded on the documents.</summary>
    public const int MaxRefusalEntriesPerDay = 50;

    /// <summary>How often orders placed for requests are checked for fills.</summary>
    public static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(30);

    /// <summary>A request claimed this long ago, with no order known to the engine or the journal, was never sent.</summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far a request's timestamp may be ahead of the engine's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A step to copy onto a request document; kept and retried if the write fails.</summary>
    private sealed record Mirror(string Id, string Day, string Status, string By, string Detail, IReadOnlyDictionary<string, string?> Outcome, bool Slim);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Mirror> _unmirrored = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private ManualLedger? _ledger;
    private bool _ledgerDirty;
    private DateTimeOffset? _lastSettle;
    private string? _lastError;

    /// <summary>
    /// <c>ok</c>; <c>off</c> (disabled, or a Live engine without <see cref="ManualRequestOptions.AllowLive"/>);
    /// <c>unavailable</c> (no requests container); or <c>not-used</c> (no database).
    /// </summary>
    public string State => !options.Enabled || (trading.Mode == TradingMode.Live && !options.AllowLive) ? "off" : store.State;

    public DateTimeOffset? LastPollAt { get; private set; }

    /// <summary>Whether the active spec lets the trader act on requests.</summary>
    public bool TraderCanAct => specs.Active?.Allows(TraderTool) == true;

    /// <summary>Agent-assisted requests waiting for the trader, oldest first.</summary>
    public IReadOnlyList<PendingRequest> Pending =>
        (Volatile.Read(ref _ledger)?.Active.Values ?? [])
            .Where(e => e.Status == ManualRequestStatus.AwaitingAgent)
            .OrderBy(e => e.Since).ThenBy(e => e.Request.Id, StringComparer.Ordinal)
            .Select(e => new PendingRequest(e.Request, e.Since, e.Since + options.AgentAnswerTimeout))
            .ToList();

    /// <summary>
    /// The waiting requests as a stable key (<c>manual:&lt;id&gt;|...</c>), empty when there are none or the trader can't act on
    /// them. The trader loop starts a run when it holds an item it hasn't handled.
    /// </summary>
    public string AgentKey => TraderCanAct
        ? string.Join('|', Pending.Take(MaxPending).Select(p => "manual:" + p.Request.Id).Order(StringComparer.Ordinal))
        : string.Empty;

    private string EngineMode => trading.Mode.ToString().ToLowerInvariant();

    /// <summary>One pass: new requests, expired waits, then fills of orders placed for requests. Returns how many requests changed.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (State != "ok")
            return 0;
        if (!execution.HoldsLease)
            return 0; // a refusal would be final, while the engine that holds the lease can still carry the request out

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var ledger = await LedgerAsync(cancellationToken).ConfigureAwait(false);
            if (ledger.Day != today)
            {
                await CommitAsync(ledger.ForDay(today), mustPersist: true, cancellationToken).ConfigureAwait(false);
                _refused.RemoveWhere(k => string.CompareOrdinal(k, today.AddDays(-1).ToString("yyyy-MM-dd", Invariant)) < 0); // outside the query window
            }
            else if (_ledgerDirty)
                await CommitAsync(ledger, mustPersist: true, cancellationToken).ConfigureAwait(false);
            await RetryMirrorsAsync(cancellationToken).ConfigureAwait(false);

            var changed = 0;
            var fresh = await store.ListNewAsync(today.AddDays(-1), today.AddDays(1), EngineMode, cancellationToken).ConfigureAwait(false);
            foreach (var item in fresh
                         .Where(r => ManualRequestDocument.ModeOf(r.Document) == EngineMode)
                         .Where(r => !_ledger!.Active.ContainsKey(r.Id) && !_ledger.HasSeen(r.Day, r.Id) && !_refused.Contains(Key(r.Day, r.Id)))
                         .OrderBy(r => ManualRequestDocument.Field(r.Document, "createdAt"), StringComparer.Ordinal)
                         .ThenBy(r => r.Id, StringComparer.Ordinal)
                         .Take(MaxPerPoll)
                         .ToList())
            {
                try
                {
                    await HandleNewAsync(item, now, cancellationToken).ConfigureAwait(false);
                    changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogRequestFailed(logger, ManualRequestDocument.SafeIdOrInvalid(item.Id), ex.GetType().Name); // the others still run
                }
            }

            foreach (var entry in _ledger!.Active.Values.Where(e => e.Status == ManualRequestStatus.AwaitingAgent && now - e.Since >= options.AgentAnswerTimeout).ToList())
            {
                await FinishAsync(entry.Request, ManualRequestStatus.Expired, EngineActor, "no-answer",
                    Inv($"The trader didn't answer within {options.AgentAnswerTimeout.TotalMinutes:0} minutes."), null, null, cancellationToken).ConfigureAwait(false);
                changed++;
            }

            if (_lastSettle is null || now - _lastSettle >= SettleInterval)
            {
                changed += await SettleAsync(now, cancellationToken).ConfigureAwait(false);
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
    /// <paramref name="portion"/>) or <c>decline</c>. A buy needs a stop-loss, which becomes the position's plan. An answer
    /// that can't be carried out as given comes back as <c>invalid-answer</c> and leaves the request waiting.
    /// </summary>
    public async Task<ManualOutcome> AnswerAsync(
        string requestId, string decision, decimal? portion, decimal? stopLoss, decimal? takeProfit, string rationale, string? rationaleRu, string agent,
        string? runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (State != "ok")
            return new ManualOutcome("unavailable", "manual-requests-off", "Manual requests are not available right now.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LedgerAsync(cancellationToken).ConfigureAwait(false);
            if (!ledger.Active.TryGetValue(requestId, out var entry) || entry.Status != ManualRequestStatus.AwaitingAgent
                || time.GetUtcNow() - entry.Since >= options.AgentAnswerTimeout)
                return new ManualOutcome("not-pending", "not-pending", "No request with that ID is waiting for an answer; it may have expired or been answered.");
            var request = entry.Request;

            if (decision == "decline")
            {
                await FinishAsync(request, ManualRequestStatus.Declined, agent, "declined", rationale,
                    new Dictionary<string, string?> { ["decidedBy"] = agent, ["runId"] = runId, ["rationale"] = rationale, ["rationaleRu"] = rationaleRu },
                    runId, cancellationToken).ConfigureAwait(false);
                return new ManualOutcome(ManualRequestStatus.Declined, "declined", "Recorded: the request is declined.");
            }

            var share = request.IsSell ? portion ?? request.Portion : portion;
            if (market.Quote(request.Symbol) is not { } quote)
                return new ManualOutcome("invalid-answer", "no-quote", $"No current price for {request.Symbol}; try again shortly or decline.");
            if (AnswerProblem(request, share, stopLoss, takeProfit, quote) is { } invalid)
                return new ManualOutcome("invalid-answer", "invalid", invalid);

            return await PlaceForRequestAsync(request, share, stopLoss, takeProfit, AgentSource, agent, runId, rationale, cancellationToken, rationaleRu: rationaleRu)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleNewAsync(StoredRequest item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ManualRequestDocument.ShapeProblem(item.Document) is { } shape)
        {
            var slim = shape.Contains("large", StringComparison.Ordinal); // drop content refused as too big rather than write it back
            await RefuseAsync(item, null, ManualRequestStatus.Invalid, "invalid", shape, slim, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (request, problem) = ManualRequestDocument.Parse(item.Document, tradable?.Current ?? TradableSymbols.None);
        if (request is null)
        {
            await RefuseAsync(item, null, ManualRequestStatus.Invalid, "invalid", problem!, slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.CreatedAt > now + ClockSkew)
        {
            await RefuseAsync(item, request, ManualRequestStatus.Invalid, "invalid", "createdAt is in the future.", slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (now - request.CreatedAt > options.MaxAgeAtPickup)
        {
            var reason = Inv($"The request was {(now - request.CreatedAt).TotalMinutes:0} minutes old when the engine saw it (at most {options.MaxAgeAtPickup.TotalMinutes:0}); not acted on. Send it again if it still applies.");
            await RefuseAsync(item, request, ManualRequestStatus.Expired, "stale", reason, slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ledger = _ledger!;
        if (ledger.Accepted >= options.MaxPerDay)
        {
            await RefuseAsync(item, request, ManualRequestStatus.Rejected, "daily-cap", Inv($"At most {options.MaxPerDay} manual requests a day (ManualRequests:MaxPerDay)."),
                slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ledger.Active.Values.Any(e => e.Request.Symbol == request.Symbol && e.Request.Side == request.Side))
        {
            await RefuseAsync(item, request, ManualRequestStatus.Duplicate, "duplicate", $"Another {request.Side} request for {request.Symbol} is still in progress.",
                slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Mode == ManualRequest.Direct && market.Quote(request.Symbol) is null)
            return; // no price yet (the engine just started): left new, and taken at a later poll or expired as stale

        if (request.Mode == ManualRequest.Agent && !TraderCanAct)
        {
            await RefuseAsync(item, request, ManualRequestStatus.Rejected, "no-trader",
                "The active strategy doesn't let the trader place orders, so it can't act on requests.", slim: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Mode == ManualRequest.Agent)
        {
            var waiting = ledger.With(new LedgerEntry(request, ManualRequestStatus.AwaitingAgent, now)).WithSeen(request.Day, request.Id);
            await CommitAsync(waiting with { Accepted = ledger.Accepted + 1 }, mustPersist: true, cancellationToken).ConfigureAwait(false);
        }

        await JournalAsync("owner", null, Inv($"Owner request {request.Id}: {request.Describe()}{(request.Note is null ? string.Empty : $" ({request.Note})")}"),
            Data(request, "received", "received", "Received from " + request.RequestedBy + ".")).ConfigureAwait(false);

        if (request.Mode == ManualRequest.Agent)
        {
            await RecordAsync(request, ManualRequestStatus.AwaitingAgent, EngineActor, "sent-to-trader", "Sent to the trader to analyse.", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PlaceForRequestAsync(request, request.Portion, null, null, DirectSource, EngineActor, null,
            request.Note is null ? "Owner's direct sell." : $"Owner's direct sell: {request.Note}", cancellationToken, firstSight: true).ConfigureAwait(false);
    }

    /// <summary>Prices and sizes the order, claims the request in the ledger, sends the order through the execution service and records the outcome.</summary>
    private async Task<ManualOutcome> PlaceForRequestAsync(
        ManualRequest request, decimal? portion, decimal? stopLoss, decimal? takeProfit, string source, string decidedBy, string? runId, string rationale,
        CancellationToken cancellationToken, bool firstSight = false, string? rationaleRu = null)
    {
        var extra = new Dictionary<string, string?>
        {
            ["decidedBy"] = decidedBy, ["runId"] = runId, ["rationale"] = rationale, ["rationaleRu"] = rationaleRu, ["source"] = source,
        };

        // A direct sell is accepted (counted and remembered) in the same save as its claim, or with its refusal.
        ManualLedger Accepted(ManualLedger ledger) =>
            firstSight ? ledger.WithSeen(request.Day, request.Id) with { Accepted = ledger.Accepted + 1 } : ledger;

        async Task<ManualOutcome> Refuse(string rule, string reason)
        {
            if (firstSight)
                await CommitAsync(Accepted(_ledger!), mustPersist: false, cancellationToken).ConfigureAwait(false);
            await FinishAsync(request, ManualRequestStatus.Rejected, decidedBy, rule, reason, extra, runId, cancellationToken).ConfigureAwait(false);
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
            price = quote.Bid;
            extra["portion"] = share.ToString(Invariant);
        }
        else
        {
            price = quote.Ask;
            if (views?.Current is { } view)
            {
                var at = time.GetUtcNow();
                if (view.BlocksEntries(at))
                    return await Refuse("market-view", Inv($"New buys are paused by the market view (risk-off) until {view.ValidUntil:yyyy-MM-dd HH:mm} UTC.")).ConfigureAwait(false);
                if (view.FlagFor(request.Symbol, at) is { } flag)
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

        // Claim in the engine's own ledger before sending: if anything stops after this, the request is resolved from the
        // claim and never sent twice. A failure to save it throws here, and nothing is sent.
        var clientOrderId = Guid.NewGuid().ToString();
        var now = time.GetUtcNow();
        var claim = new LedgerEntry(request, ManualRequestStatus.Executing, now, clientOrderId, now, decidedBy, runId);
        await CommitAsync(Accepted(_ledger!).With(claim), mustPersist: true, cancellationToken).ConfigureAwait(false);
        extra["clientOrderId"] = clientOrderId;
        extra["side"] = request.Side;
        extra["requestedSize"] = size.ToString(Invariant);
        extra["requestedPrice"] = price.ToString(Invariant);

        var side = request.IsSell ? OrderSide.Sell : OrderSide.Buy;
        var result = await execution.PlaceAsync(
            new OrderIntent(request.Symbol, side, size, price, clientOrderId), postOnly: false, CancellationToken.None, // claimed: see it through
            countsTowardRejectStreak: !request.IsSell, // a sell never adds risk, as for the engine's protective sells
            context: new OrderContext(source, stopLoss, takeProfit, runId, specs.Active?.Label),
            strategyFiltered: source == AgentSource).ConfigureAwait(false); // an agent-assisted buy follows the strategy's filters

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
        if (rationaleRu is not null)
            orderData["rationaleRu"] = rationaleRu; // for the owner's dashboard only
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

        await AppendAsync(new JournalEntry(
            JournalIds.New(time.GetUtcNow(), "order"), time.GetUtcNow(), "order", decidedBy, runId, specs.Active?.Label,
            Inv($"manual {request.Side} {size} {request.Symbol} @ {price}: {result.Status} ({result.Rule})"), orderData)).ConfigureAwait(false);

        // The order exists (or may): record the outcome whatever the caller's token says.
        if (status is ManualRequestStatus.Rejected or ManualRequestStatus.Failed)
        {
            await FinishAsync(request, status, decidedBy, result.Rule, result.Reason, extra, runId, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await CommitAsync(_ledger!.With(claim with { Status = status, Since = time.GetUtcNow() }), mustPersist: false, CancellationToken.None).ConfigureAwait(false);
            await RecordAsync(request, status, decidedBy, result.Rule, result.Reason, extra, runId, CancellationToken.None).ConfigureAwait(false);
        }

        return new ManualOutcome(status, result.Rule, result.Reason, clientOrderId, result.Order?.BaseSize ?? size, result.Order?.LimitPrice ?? price,
            result.Placed?.VenueOrderId);
    }

    /// <summary>
    /// Why the answer can't be carried out as given, or null: a sell's portion must be above 0 and at most what the owner
    /// asked, and takes no stop or target; a buy takes no portion and needs a stop (and any target) a real distance from the
    /// ask, as place_order does.
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
        if (takeProfit is { } target && (target < ask * (1 + distance) || target > Tools.OrderTools.MaxPrice))
            return Inv($"takeProfit must be at least {distance:P1} above the ask ({ask}) and at most {Tools.OrderTools.MaxPrice}.");
        return null;
    }

    /// <summary>Moves claimed and placed requests to filled or closed once the journal has their order's outcome; resolves claims a restart interrupted.</summary>
    private async Task<int> SettleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var entry in _ledger!.Active.Values.Where(e => e.ClientOrderId is not null).ToList())
        {
            var clientOrderId = entry.ClientOrderId!;
            var keep = new Dictionary<string, string?> { ["clientOrderId"] = clientOrderId };
            var day = DateOnly.FromDateTime((entry.ClaimedAt ?? entry.Since).UtcDateTime);
            var entries = await journal.QueryAsync(new JournalQuery(From: day, ClientOrderId: clientOrderId, Limit: 50), cancellationToken).ConfigureAwait(false);
            if (entries.FirstOrDefault(e => e.Kind is "fill" or "order-closed") is { } end)
            {
                var filled = end.Kind == "fill";
                keep["filledSize"] = end.Data?.GetValueOrDefault("filledSize");
                keep["averagePrice"] = end.Data?.GetValueOrDefault("averagePrice");
                keep["orderStatus"] = end.Data?.GetValueOrDefault("status");
                await FinishAsync(entry.Request, filled ? ManualRequestStatus.Filled : ManualRequestStatus.Closed, EngineActor, end.Kind,
                    filled ? $"Filled {end.Data?.GetValueOrDefault("filledSize")} at {end.Data?.GetValueOrDefault("averagePrice")}." : "The order ended without a fill.",
                    keep, entry.RunId, cancellationToken).ConfigureAwait(false);
                changed++;
                continue;
            }

            if (entry.Status == ManualRequestStatus.Placed && now - (entry.ClaimedAt ?? entry.Since) >= RestingCancelAfter && execution.IsWorkingOrder(clientOrderId))
            {
                changed += await CancelRestingAsync(entry, clientOrderId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (entry.Status != ManualRequestStatus.Executing)
            {
                // Placed or unconfirmed: the reconciler journals how it ends. If the engine stopped tracking the order without
                // a record (an unconfirmed placement it never found, or one it gave up looking up), close the request rather
                // than leave it blocking the next request for this coin, and say what is unknown.
                if (!IsTracked(clientOrderId) && now - entry.Since >= UntrackedAfter)
                {
                    await FinishAsync(entry.Request, ManualRequestStatus.Closed, EngineActor, "outcome-unknown", OutcomeUnknown, keep, entry.RunId, cancellationToken)
                        .ConfigureAwait(false);
                    changed++;
                }

                continue;
            }

            // Claimed, and the outcome never recorded: the engine stopped while sending.
            var order = entries.FirstOrDefault(e => e.Kind == "order");
            var orderStatus = order?.Data?.GetValueOrDefault("status");
            if (IsTracked(clientOrderId) || orderStatus is "placed" or "uncertain")
            {
                await CommitAsync(_ledger!.With(entry with { Status = ManualRequestStatus.Unconfirmed, Since = now }), mustPersist: false, cancellationToken)
                    .ConfigureAwait(false);
                await RecordAsync(entry.Request, ManualRequestStatus.Unconfirmed, EngineActor, "restarted",
                    "The engine restarted while handling this request; the order is being reconciled.", keep, entry.RunId, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            else if (orderStatus is "rejected" or "failed")
            {
                await FinishAsync(entry.Request, orderStatus == "rejected" ? ManualRequestStatus.Rejected : ManualRequestStatus.Failed, EngineActor,
                    order!.Data!.GetValueOrDefault("rule") ?? "unknown", order.Data.GetValueOrDefault("reason") ?? "See the journal.", keep, entry.RunId, cancellationToken)
                    .ConfigureAwait(false);
                changed++;
            }
            else if (now - (entry.ClaimedAt ?? entry.Since) >= ClaimGrace)
            {
                // No positive evidence either way: never tell the owner "nothing was sent", which could lead to selling twice.
                await FinishAsync(entry.Request, ManualRequestStatus.Closed, EngineActor, "outcome-unknown", OutcomeUnknown, keep, entry.RunId, cancellationToken)
                    .ConfigureAwait(false);
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// A manual order still unfilled after this is cancelled (the request then closes with what filled): a resting sell
    /// mustn't hold a position past its stop, and a resting buy isn't what the owner asked for.
    /// </summary>
    public static readonly TimeSpan RestingCancelAfter = TimeSpan.FromMinutes(4);

    private async Task<int> CancelRestingAsync(LedgerEntry entry, string clientOrderId, CancellationToken cancellationToken)
    {
        var open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (open.FirstOrDefault(o => o.IsOpen && o.ClientOrderId == clientOrderId) is not { } order)
            return 0; // finished meanwhile: the reconciler journals it
        var (outcome, _) = await execution.CancelOwnOrderAsync(order.VenueOrderId, cancellationToken).ConfigureAwait(false);
        if (entry.CancelRequested || outcome != CancelOutcome.Cancelled)
            return 0;
        await CommitAsync(_ledger!.With(entry with { CancelRequested = true }), mustPersist: false, cancellationToken).ConfigureAwait(false);
        await JournalAsync(EngineActor, entry.RunId, $"Owner request {entry.Request.Id}: unfilled order cancelled after {RestingCancelAfter.TotalMinutes:0} minutes",
            Data(entry.Request, ManualRequestStatus.Placed, "cancel-unfilled", "The order didn't fill at once; cancelled so it can't hold the position.")).ConfigureAwait(false);
        return 1;
    }

    private const string OutcomeUnknown =
        "The engine has no record of how this order ended (it stopped while sending it, or lost track of it). Check the exchange's order history before sending the request again.";

    /// <summary>Whether the execution service still knows the order: working, or sent with an outcome it is still resolving.</summary>
    private bool IsTracked(string clientOrderId) =>
        execution.IsWorkingOrder(clientOrderId) || execution.Unresolved.Any(p => p.Order.ClientOrderId == clientOrderId);

    /// <summary>A placed request whose order the engine no longer tracks, and whose end isn't journaled, is closed after this.</summary>
    public static readonly TimeSpan UntrackedAfter = TimeSpan.FromMinutes(15);

    /// <summary>A request ends: out of the ledger, into the journal, and onto its document.</summary>
    private async Task FinishAsync(
        ManualRequest request, string status, string by, string rule, string reason, IReadOnlyDictionary<string, string?>? extra, string? runId,
        CancellationToken cancellationToken)
    {
        if (_ledger!.Active.TryGetValue(request.Id, out var entry))
        {
            // A decision on a waiting request (declined, refused, expired) is saved before it is reported, so a restart
            // can't bring the request back. After an order, the outcome is recorded whatever happens to the save.
            var waiting = entry.Status == ManualRequestStatus.AwaitingAgent;
            await CommitAsync(_ledger.Without(request.Id), mustPersist: waiting, waiting ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        }

        await RecordAsync(request, status, by, rule, reason, extra, runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Journals a step and copies it onto the request document.</summary>
    private async Task RecordAsync(
        ManualRequest request, string status, string by, string rule, string reason, IReadOnlyDictionary<string, string?>? extra, string? runId,
        CancellationToken cancellationToken)
    {
        var outcome = Outcome(extra, rule, reason);
        var data = Data(request, status, rule, outcome["reason"] ?? string.Empty);
        foreach (var key in new[] { "clientOrderId", "rationale", "rationaleRu", "runId", "portion", "filledSize", "averagePrice" })
        {
            if (outcome.GetValueOrDefault(key) is { Length: > 0 } value)
                data[key] = value;
        }

        await JournalAsync(by, runId, $"Owner request {request.Id} ({request.Describe()}): {status} ({rule})", data).ConfigureAwait(false);
        QueueMirror(request, status, by, reason, outcome);
        await RetryMirrorsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A request refused at first sight: never in the ledger; journaled (up to a daily limit) and marked on its document.</summary>
    private async Task RefuseAsync(StoredRequest item, ManualRequest? request, string status, string rule, string reason, bool slim, CancellationToken cancellationToken)
    {
        _refused.Add(Key(item.Day, item.Id));
        var ledger = _ledger! with { Refused = _ledger!.Refused + 1 };
        await CommitAsync(ledger, mustPersist: false, cancellationToken).ConfigureAwait(false);
        var id = ManualRequestDocument.SafeIdOrInvalid(item.Id);
        if (ledger.Refused <= MaxRefusalEntriesPerDay)
        {
            var data = request is null
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["requestId"] = id, ["status"] = status, ["rule"] = rule, ["reason"] = ManualRequestDocument.Clean(reason, 1_000) ?? rule }
                : Data(request, status, rule, reason);
            await JournalAsync(EngineActor, null, $"Owner request {id}: {status} ({rule})", data).ConfigureAwait(false);
        }
        else if (ledger.Refused == MaxRefusalEntriesPerDay + 1)
        {
            LogRefusalsCapped(logger, MaxRefusalEntriesPerDay);
        }

        var mirror = new Mirror(item.Id, item.Day, status, EngineActor, reason, Outcome(null, rule, reason), slim);
        _unmirrored[Key(item.Day, item.Id)] = mirror;
        await TryMirrorAsync(mirror, item, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> Outcome(IReadOnlyDictionary<string, string?>? extra, string rule, string reason) =>
        new(extra ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
        {
            ["rule"] = rule,
            ["reason"] = ManualRequestDocument.Clean(reason, 1_000),
        };

    private void QueueMirror(ManualRequest request, string status, string by, string detail, IReadOnlyDictionary<string, string?> outcome) =>
        _unmirrored[Key(request.Day, request.Id)] = new Mirror(request.Id, request.Day, status, by, detail, outcome, Slim: false);

    /// <summary>Copies pending steps onto their request documents. A failure keeps the step for the next poll and never stops trading.</summary>
    private async Task RetryMirrorsAsync(CancellationToken cancellationToken)
    {
        foreach (var mirror in _unmirrored.Values.Take(MaxMirrorsPerPass).ToList())
            await TryMirrorAsync(mirror, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Document copies written per pass: a backlog (a throttled database) catches up gradually rather than all at once.</summary>
    public const int MaxMirrorsPerPass = 10;

    private async Task TryMirrorAsync(Mirror mirror, StoredRequest? current, CancellationToken cancellationToken)
    {
        var key = Key(mirror.Day, mirror.Id);
        try
        {
            var document = current ?? await store.GetAsync(mirror.Id, mirror.Day, cancellationToken).ConfigureAwait(false);
            if (document is null || ManualRequestDocument.Status(document.Document) == mirror.Status)
            {
                _unmirrored.Remove(key); // gone, or already written
                return;
            }

            var source = mirror.Slim
                ? new JsonObject { ["id"] = mirror.Id, ["day"] = mirror.Day, ["type"] = ManualRequestDocument.Type } // content refused as too large isn't kept
                : document.Document;
            var next = ManualRequestDocument.WithStatus(source, mirror.Status, time.GetUtcNow(), mirror.By, mirror.Detail, mirror.Outcome);
            if (await store.TryReplaceAsync(document, next, cancellationToken).ConfigureAwait(false) is not null && _unmirrored.GetValueOrDefault(key) == mirror)
                _unmirrored.Remove(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogMirrorFailed(logger, ManualRequestDocument.SafeIdOrInvalid(mirror.Id), ex.GetType().Name);
        }
    }

    private async Task<ManualLedger> LedgerAsync(CancellationToken cancellationToken)
    {
        if (_ledger is null)
            Volatile.Write(ref _ledger, await ledgerStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? ManualLedger.Empty);
        return _ledger!;
    }

    /// <summary>
    /// Replaces the ledger. With <paramref name="mustPersist"/>, it changes only once saved (a claim must be saved before its
    /// order is sent). Otherwise it changes at once and a failed save is retried at the next poll.
    /// </summary>
    private async Task CommitAsync(ManualLedger next, bool mustPersist, CancellationToken cancellationToken)
    {
        if (mustPersist)
        {
            await ledgerStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _ledger, next);
            _ledgerDirty = false;
            return;
        }

        Volatile.Write(ref _ledger, next);
        try
        {
            await ledgerStore.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            _ledgerDirty = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ledgerDirty = true;
            LogLedgerFailed(logger, ex.GetType().Name);
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
        if (request.Note is { } note)
            data["note"] = note;
        return data;
    }

    private static string Key(string day, string id) => day + "/" + id;

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
            // The first poll runs at once, so /signals reports requests still waiting for the trader right after a restart.
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
                        LogPollFailed(logger, ex.GetType().Name, ex.Message); // once per kind of failure, not every few seconds
                    _lastError = ex.GetType().Name;
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual requests are off: the database has no requests container. Run tools/azure/provision.ps1 again, then restart the engine")]
    private static partial void LogNotRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual requests poll failed ({Error}): {Detail}")]
    private static partial void LogPollFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual request {Id} could not be handled ({Error}); retried at the next poll")]
    private static partial void LogRequestFailed(ILogger logger, string id, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual request {Id}: its document could not be updated ({Error}); retrying")]
    private static partial void LogMirrorFailed(ILogger logger, string id, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The manual request ledger could not be saved ({Error}); retrying at the next poll")]
    private static partial void LogLedgerFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "More than {Limit} manual requests refused today: further refusals are recorded on the documents only")]
    private static partial void LogRefusalsCapped(ILogger logger, int limit);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for a {Kind} entry ({Error})")]
    private static partial void LogJournalFailed(ILogger logger, string kind, string error);
}

public static class ManualRequestRegistration
{
    /// <summary>The service and its poll loop, over in-memory stores until Cosmos DB replaces them (AddCixCosmos).</summary>
    public static IServiceCollection AddCixManualRequests(this IServiceCollection services, ManualRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IManualRequestStore>(new InMemoryManualRequestStore());
        services.AddSingleton<IManualLedgerStore>(new InMemoryManualLedgerStore());
        services.AddSingleton<ManualRequestService>();
        services.AddHostedService(sp => sp.GetRequiredService<ManualRequestService>());

        // The owner's Pause and Resume buttons (ADR-0018), in both modes: the same container, its own request type.
        services.AddSingleton<TradingControlService>();
        services.AddHostedService(sp => sp.GetRequiredService<TradingControlService>());
        return services;
    }
}
