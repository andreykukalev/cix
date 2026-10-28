using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Persistence;

namespace Cix.Engine.Execution;

public enum ExecutionStatus
{
    /// <summary>The exchange accepted the order.</summary>
    Placed,

    /// <summary>The engine or the risk gate refused it; nothing was sent.</summary>
    Rejected,

    /// <summary>Sent, but the outcome is unknown and the order wasn't found; it is held until reconciled.</summary>
    Uncertain,

    /// <summary>Sent and refused by the exchange.</summary>
    Failed,
}

/// <param name="Rule">The gate's rule, or the engine's reason code (for example <c>kill-switch</c>, <c>portfolio-unavailable</c>).</param>
/// <param name="Order">What was approved and sent; null when rejected before approval.</param>
public sealed record ExecutionResult(ExecutionStatus Status, string Rule, string Reason, ApprovedOrder? Order = null, PlacedOrder? Placed = null);

/// <summary>
/// What a stop-loss sell can take now: the free coins above the exchange minimum, and whether a position is still held at all
/// (free or reserved by an open order). Coins held but not free are an order to cancel, not a finished exit.
/// </summary>
public sealed record SellableNow(decimal Sellable, bool HoldsPosition);

/// <summary>The account as <see cref="ExecutionService.RunExclusiveAsync"/> sees it: fresh reads, and orders sent but not settled.</summary>
public sealed record ExclusiveView(
    Func<CancellationToken, Task<IReadOnlyList<ExchangeOrder>>> OpenOrdersAsync,
    Func<CancellationToken, Task<PortfolioSnapshot?>> PortfolioAsync,
    IReadOnlyList<PendingPlacement> Pending);

public enum CancelOutcome
{
    Cancelled,
    NotFound,
    NotOwn,
    Failed,
}

/// <summary>
/// Why an order is sent and the plan that goes with it. Kept in the engine state while the order works (so a restart keeps
/// it) and copied into the journal's fill entry.
/// </summary>
/// <param name="Source">
/// <c>place_order</c>, <c>protective-exit</c>, <c>manual-direct</c> (an owner's direct sell), <c>manual-agent</c> (an owner's
/// request the trader agreed to) or <c>engine</c>.
/// </param>
public sealed record OrderContext(string Source, decimal? StopLoss = null, decimal? TakeProfit = null, string? RunId = null, string? Strategy = null);

/// <summary>A portfolio valuation and when it was taken.</summary>
public sealed record PortfolioSample(PortfolioSnapshot Snapshot, DateTimeOffset At);

/// <summary>
/// The latest reconciliation, for status reporting. <paramref name="Problem"/> is a fixed code, never free text:
/// <c>exchange-unreadable</c>, <c>unknown-open-orders</c>, <c>unexplained-holdings-change</c>, <c>holdings-not-compared</c>
/// (no fresh prices to value them), or null when all was well. The details are in the journal and the logs.
/// </summary>
/// <param name="ReconciledAt">The last reconciliation that read the exchange and compared it fully.</param>
public sealed record ReconcileStatus(DateTimeOffset? ReconciledAt, DateTimeOffset AttemptedAt, string? Problem);

/// <summary>What engaging the kill switch did to open orders.</summary>
public sealed record KillSwitchResult(bool NewlyEngaged, int Cancelled, IReadOnlyList<string> NotCancelled);

/// <summary>
/// The only way the engine places orders: round to the pair's steps, value the portfolio, ask the <see cref="RiskGate"/>,
/// then send what it approved. Placements run one at a time, so two orders can't both pass the gate against the same
/// snapshot. An order whose outcome is unknown is looked up by client order ID, never resent.
/// </summary>
public sealed partial class ExecutionService : IActiveSymbolSource
{
    public const string RejectStreakBy = "execution";

    /// <summary>How long after an order was sent its coin stays active (polled order books), until the next sample shows it held or working.</summary>
    public static readonly TimeSpan RecentOrderWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long a sent order counts as pending exposure while it may not show in open orders yet.</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan[] ReconcileDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly IExchangeClient _exchange;
    private readonly RiskGate _gate;
    private readonly PortfolioService _portfolio;
    private readonly MarketState _market;
    private readonly PairCatalog _pairs;
    private readonly KillSwitch _killSwitch;
    private readonly IEngineStateStore _store;
    private readonly SafetyOptions _safety;
    private readonly IJournal? _journal;
    private readonly IEngineLease _lease;
    private readonly TimeProvider _time;
    private readonly ILogger<ExecutionService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, CancellationToken, Task<MarketQuote?>>? _refreshQuote;
    private readonly TimeSpan _freshBookWithin;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Coins an order was sent in lately, by symbol: active for the market data until a sample shows them held or working.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _recentOrders = new(StringComparer.Ordinal);
    /// <summary>Orders sent in the last minute, and which were protective exits: those don't use the trading rate (MaxOrdersPerMinute).</summary>
    private readonly Queue<(DateTimeOffset At, bool Protective)> _sent = new();
    private readonly Dictionary<string, PendingPlacement> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedClientOrderIds = new(StringComparer.Ordinal);

    /// <summary>Orders this process placed and hasn't yet seen finish; reconciliation journals their fills.</summary>
    private readonly Dictionary<string, TrackedOrder> _tracked = new(StringComparer.Ordinal);

    /// <param name="Restored">Rebuilt from the engine state after a restart: its fill may already be journaled.</param>
    private sealed record TrackedOrder(OrderIntent Order, string VenueOrderId, int Lookups = 0, bool Restored = false);
    private int _consecutiveRejects;
    private int _streakTripped;

    /// <summary>When the engine last sent a buy, for the strategy's cooldown. After a restart: the latest working buy or open plan.</summary>
    private DateTimeOffset? _lastBuyAt;

    /// <summary>Buys sent per agent run, for the strategy's positions-per-run limit. Only the latest runs are kept.</summary>
    private readonly Dictionary<string, int> _buysByRun = new(StringComparer.Ordinal);
    private readonly Queue<string> _runOrder = new();
    private const int RunsRemembered = 50;

    /// <summary>Agent buys without a run ID count together, so leaving out <c>X-Cix-Run</c> doesn't lift the per-run limit.</summary>
    private const string NoRun = "(no run)";

    /// <summary>The order source of agents' buys, the only ones the strategy's limits count.</summary>
    private const string AgentSource = "place_order";

    public ExecutionService(
        IExchangeClient exchange,
        RiskGate gate,
        PortfolioService portfolio,
        MarketState market,
        PairCatalog pairs,
        KillSwitch killSwitch,
        IEngineStateStore store,
        TimeProvider time,
        ILogger<ExecutionService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        SafetyOptions? safety = null,
        IJournal? journal = null,
        IEngineLease? lease = null,
        Func<string, CancellationToken, Task<MarketQuote?>>? refreshQuote = null,
        TimeSpan? freshBookWithin = null)
    {
        _exchange = exchange;
        _refreshQuote = refreshQuote;
        _freshBookWithin = freshBookWithin ?? TimeSpan.FromSeconds(20);
        _gate = gate;
        _portfolio = portfolio;
        _market = market;
        _pairs = pairs;
        _killSwitch = killSwitch;
        _store = store;
        _safety = safety ?? new SafetyOptions();
        _safety.Validate();
        _journal = journal;
        _lease = lease ?? NoEngineLease.Instance;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));

        // After a restart, orders sent before it are still ours: their IDs can't be reused and aren't "unknown". Those the
        // exchange confirmed are tracked again, so a fill that happened while the engine was down is still journaled.
        _usedClientOrderIds.UnionWith(store.Current.Known);
        foreach (var sent in store.Current.SentOrders.Where(o => o.VenueOrderId is not null))
            _tracked[sent.ClientOrderId] = new TrackedOrder(ToIntent(sent), sent.VenueOrderId!, Restored: true);
        // The strategy's cooldown and per-run count go on from the agents' buys the state still holds.
        var agentBuys = store.Current.SentOrders.Where(o => o.Side == "buy" && o.Source == AgentSource).OrderBy(o => o.SentAt).ToList();
        _lastBuyAt = agentBuys.Select(o => (DateTimeOffset?)o.SentAt)
            .Concat(store.Current.Plans.Values.Select(p => (DateTimeOffset?)p.Since))
            .Max();
        foreach (var buy in agentBuys)
            CountBuy(buy.RunId);
    }

    /// <summary>When the engine last sent a buy, if it knows (see <see cref="StrategyActivity"/>).</summary>
    public DateTimeOffset? LastBuyAt
    {
        get
        {
            lock (_buysByRun)
                return _lastBuyAt;
        }
    }

    internal StrategyActivity ActivityFor(string? runId)
    {
        lock (_buysByRun)
            return new StrategyActivity(_lastBuyAt, _buysByRun.GetValueOrDefault(runId ?? NoRun));
    }

    private void NoteBuySent(string? runId, DateTimeOffset at)
    {
        lock (_buysByRun)
        {
            _lastBuyAt = at;
            CountBuy(runId);
        }
    }

    private void CountBuy(string? runId)
    {
        var key = runId ?? NoRun;
        if (!_buysByRun.TryGetValue(key, out var count))
        {
            _runOrder.Enqueue(key);
            if (_runOrder.Count > RunsRemembered)
                _buysByRun.Remove(_runOrder.Dequeue());
        }
        _buysByRun[key] = count + 1;
    }

    /// <summary>Whether this engine holds the engine lease for its mode, and so may act on the exchange (ADR-0013).</summary>
    public bool HoldsLease => _lease.IsHeld;

    /// <summary>Whether the engine sent an order with this client order ID that it hasn't yet seen finish.</summary>
    public bool IsWorkingOrder(string clientOrderId) => _store.Current.Known.Contains(clientOrderId, StringComparer.Ordinal);

    /// <summary>Position plans (stop and target by symbol) from the engine state.</summary>
    public IReadOnlyDictionary<string, PositionPlanState> PositionPlanStates => _store.Current.Plans;

    private static OrderIntent ToIntent(SentOrder o) =>
        new(o.Symbol, o.Side == "sell" ? OrderSide.Sell : OrderSide.Buy, o.Size, o.LimitPrice, o.ClientOrderId);

    private static string SideName(OrderSide side) => side == OrderSide.Sell ? "sell" : "buy";

    /// <summary>Orders sent whose outcome is unknown; they block reuse of their client order ID until reconciled.</summary>
    public IReadOnlyList<PendingPlacement> Unresolved
    {
        get
        {
            lock (_pending)
                return _pending.Values.Where(p => p.Uncertain).ToList();
        }
    }

    /// <param name="countsTowardRejectStreak">
    /// False for the engine's own protective sells: a sell never adds risk, and a sell the gate keeps refusing in a fast market
    /// shouldn't halt everything. Agents' orders always count.
    /// </param>
    /// <param name="context">Why the order is sent and its stop and target, kept with it (see <see cref="OrderContext"/>).</param>
    /// <param name="strategy">
    /// The active strategy's own limits, for an agent's buy: checked with <see cref="StrategyGate"/> before the risk gate, which
    /// stays final. Null for orders the strategy doesn't govern (protective sells, the owner's requests).
    /// </param>
    public async Task<ExecutionResult> PlaceAsync(
        OrderIntent intent, bool postOnly, CancellationToken cancellationToken, bool countsTowardRejectStreak = true, OrderContext? context = null,
        StrategyLimits? strategy = null, Func<CancellationToken, Task>? beforeSend = null)
    {
        var result = await PlaceLockedAsync(intent, postOnly, countsTowardRejectStreak, context, strategy, beforeSend, cancellationToken).ConfigureAwait(false);

        // Engaging takes the placement lock, so it runs after the placement released it.
        // Only a gate rejection made by this call can trip the streak (engine-side rejects never count).
        if (Interlocked.Exchange(ref _streakTripped, 0) == 1 && !_killSwitch.IsEngaged)
        {
            await EngageKillSwitchAsync(
                $"{_safety.MaxConsecutiveRejects} orders in a row were rejected by the risk gate (last: {result.Rule}).",
                RejectStreakBy,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ExecutionResult> PlaceLockedAsync(
        OrderIntent intent, bool postOnly, bool countsTowardRejectStreak, OrderContext? context, StrategyLimits? strategy,
        Func<CancellationToken, Task>? beforeSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent = intent with { Symbol = MarketDataPoller.NormaliseSymbol(intent.Symbol) };

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A halt stops new risk; the engine's stop-loss sells take risk off and go on (the gate checks the rest).
            if (_killSwitch.IsEngaged && !intent.IsProtectiveExit)
                return Reject("kill-switch", "The kill switch is engaged; no orders are allowed.");

            var now = _time.GetUtcNow();
            PruneSent(now);
            lock (_pending)
            {
                if (_pending.TryGetValue(intent.ClientOrderId ?? string.Empty, out var earlier) && earlier.Uncertain)
                    return Reject("placement-unresolved", "An earlier order with this client order ID has an unknown outcome; it must be reconciled first.");
                if (_usedClientOrderIds.Contains(intent.ClientOrderId ?? string.Empty))
                    return Reject("duplicate-client-order-id", "This client order ID was already sent; use a new one for a new order.");
            }

            PairInfo? pair;
            try
            {
                pair = await _pairs.GetAsync(intent.Symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
            {
                return Reject("pairs-unavailable", "The exchange's trading rules could not be loaded.");
            }

            if (pair is null)
                return Reject("unknown-symbol", $"The exchange doesn't list {intent.Symbol}.");

            await RefreshBookIfNeededAsync(intent.Symbol, now, cancellationToken).ConfigureAwait(false);
            var rounded = RoundToSteps(intent, pair);
            PortfolioSnapshot portfolio;
            try
            {
                // Trading orders are rated against each other; a protective exit against everything (its own backstop in the gate).
                var inLastMinute = intent.IsProtectiveExit ? _sent.Count : _sent.Count(o => !o.Protective);
                portfolio = await _portfolio.GetSnapshotAsync(inLastMinute, PendingSnapshot(), cancellationToken, skipUnpriced: intent.IsProtectiveExit)
                    .ConfigureAwait(false);
            }
            catch (PortfolioUnavailableException ex)
            {
                return Reject("portfolio-unavailable", ex.Message);
            }

            // The strategy's limits only tighten, and never replace the gate: a buy they allow (or reduce) still goes to it.
            StrategyVerdict? strategyCheck = null;
            if (strategy is not null && rounded.Side == OrderSide.Buy)
            {
                strategyCheck = StrategyGate.CheckBuy(rounded, portfolio, strategy, ActivityFor(context?.RunId), now, pair.BaseStep);
                if (strategyCheck.Order is not { } allowed)
                {
                    LogRejected(_logger, intent.ClientOrderId ?? string.Empty, strategyCheck.Rule, strategyCheck.Reason);
                    return Reject(strategyCheck.Rule, strategyCheck.Reason); // the strategy's refusal, not the gate's: no reject streak
                }
                rounded = allowed;
            }

            var verdict = _gate.Evaluate(rounded, portfolio, _market.Snapshot(rounded.Symbol), pair);
            if (verdict is { Outcome: RiskOutcome.Accepted } && strategyCheck is { Resized: true })
                verdict = verdict with { Outcome = RiskOutcome.Resized, Rule = strategyCheck.Rule, Reason = strategyCheck.Reason };
            if (verdict.Order is not { } order)
            {
                if (countsTowardRejectStreak
                    && verdict.Rule is not ("kill-switch" or "invalid-order")
                    && Interlocked.Increment(ref _consecutiveRejects) >= _safety.MaxConsecutiveRejects)
                    Interlocked.Exchange(ref _streakTripped, 1);
                LogRejected(_logger, intent.ClientOrderId ?? string.Empty, verdict.Rule, verdict.Reason);
                return new ExecutionResult(ExecutionStatus.Rejected, verdict.Rule, verdict.Reason);
            }

            // Whether the symbol is still held decides if an open position plan applies to this order (see EngineState.WithSent).
            var holdsPosition = portfolio.PositionQuoteBySymbol.GetValueOrDefault(rounded.Symbol) >= Math.Max(pair.MinOrderSizeQuote, 0.00000001m);
            var result = await SendAsync(order, verdict, postOnly, context, holdsPosition, beforeSend, cancellationToken).ConfigureAwait(false);
            if (strategy is not null && rounded.Side == OrderSide.Buy && result.Status is ExecutionStatus.Placed or ExecutionStatus.Uncertain)
                NoteBuySent(context?.RunId, now);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Values the portfolio so peak equity and the 00:00 UTC baseline follow the market, not just order attempts.
    /// Called every minute by <see cref="SafetyMonitor"/>. Returns null if the portfolio can't be valued now.
    /// </summary>
    public async Task<PortfolioSnapshot?> SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _portfolio.GetSnapshotAsync(0, PendingSnapshot(), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _latestSample, new PortfolioSample(snapshot, _time.GetUtcNow()));
            return snapshot;
        }
        catch (PortfolioUnavailableException ex)
        {
            LogSampleFailed(_logger, ex.Message);
            return null;
        }
    }

    private PortfolioSample? _latestSample;

    /// <summary>
    /// The portfolio as the safety monitor last valued it (every minute), or null before the first valuation. Reading it costs
    /// no exchange call and changes nothing, so status reporting can use it freely.
    /// </summary>
    public PortfolioSample? LatestSample => Volatile.Read(ref _latestSample);

    /// <summary>
    /// The coins whose order books the market data keeps polling: those with a position or a working buy in the latest sample,
    /// and those an order was sent in within <see cref="RecentOrderWindow"/> (the sample is a minute old at most, the order
    /// may be seconds old). Everything else prices from the tickers.
    /// </summary>
    public IReadOnlySet<string> ActiveSymbols
    {
        get
        {
            var now = _time.GetUtcNow();
            var active = new HashSet<string>(StringComparer.Ordinal);
            if (LatestSample?.Snapshot is { } snapshot)
            {
                active.UnionWith(snapshot.PositionQuoteBySymbol.Where(p => p.Value > 0m).Select(p => p.Key));
                if (snapshot.OpenBuyQuoteBySymbol is { } buys)
                    active.UnionWith(buys.Where(b => b.Value > 0m).Select(b => b.Key));
            }

            foreach (var (symbol, at) in _recentOrders)
            {
                if (now - at <= RecentOrderWindow)
                    active.Add(symbol);
                else
                    _recentOrders.TryRemove(symbol, out _);
            }

            return active;
        }
    }

    private (DateTimeOffset At, PortfolioSnapshot Snapshot)? _portfolioCache;

    /// <summary>
    /// The portfolio for tools: the latest valuation if under <see cref="OpenOrdersCacheTtl"/> old, otherwise a fresh one.
    /// Returns the snapshot and when it was taken, or null if the portfolio can't be valued now.
    /// </summary>
    public async Task<(PortfolioSnapshot Snapshot, DateTimeOffset At)?> GetPortfolioAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_portfolioCache is { } cached && now - cached.At < OpenOrdersCacheTtl)
            return (cached.Snapshot, cached.At);

        var snapshot = await SampleAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;
        _portfolioCache = (now, snapshot);
        return (snapshot, now);
    }

    /// <summary>
    /// Runs <paramref name="work"/> while no order can be placed or cancelled, with an uncached view of the account: what holds
    /// for it can't change before it ends. For changes that must not race an order, such as removing a tradable pair.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<ExclusiveView, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(new ExclusiveView(
                ct => _exchange.GetOpenOrdersAsync(null, ct),
                SampleAsync,
                PendingSnapshot())).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>How long agents' views of open orders and the portfolio may be cached, so a looping agent can't use up the exchange's rate limit.</summary>
    public static readonly TimeSpan OpenOrdersCacheTtl = TimeSpan.FromSeconds(10);

    private (DateTimeOffset At, IReadOnlyList<ExchangeOrder> Orders)? _openOrdersCache;

    /// <summary>Open orders for tools: read-only, cached for <see cref="OpenOrdersCacheTtl"/>.</summary>
    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_openOrdersCache is { } cached && now - cached.At < OpenOrdersCacheTtl)
            return cached.Orders;

        var orders = await _exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
        _openOrdersCache = (now, orders);
        return orders;
    }

    /// <summary>One order as the exchange reports it now (uncached), or null if it doesn't know it (yet).</summary>
    public Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken) =>
        _exchange.GetOrderAsync(venueOrderId, cancellationToken);

    /// <summary>
    /// The portfolio valued now, leaving out held coins without a fresh price instead of failing (see
    /// <see cref="PortfolioService.GetSnapshotAsync"/>). For the protective exits only; null if balances can't be read.
    /// </summary>
    public async Task<(PortfolioSnapshot Snapshot, DateTimeOffset At)?> GetPortfolioSkippingUnpricedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _portfolio.GetSnapshotAsync(0, PendingSnapshot(), cancellationToken, skipUnpriced: true).ConfigureAwait(false);
            return (snapshot, _time.GetUtcNow());
        }
        catch (PortfolioUnavailableException ex)
        {
            LogSampleFailed(_logger, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// What can be sold of <paramref name="symbol"/> now, read fresh from the exchange (free balance, at least the exchange
    /// minimum; 0 when nothing is), the way a protective exit sees it: other coins without a fresh price don't stop it.
    /// </summary>
    /// <exception cref="PortfolioUnavailableException">Balances or the pair rules can't be read.</exception>
    public async Task<SellableNow> SellableNowAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = MarketDataPoller.NormaliseSymbol(symbol);
        var snapshot = await _portfolio.GetSnapshotAsync(0, PendingSnapshot(), cancellationToken, skipUnpriced: true).ConfigureAwait(false);
        PairInfo? pair = null;
        try
        {
            pair = await _pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
        {
            // Without the pair's minimum, any coins at all count as a position: better a retry than a false "done".
        }

        var minimum = Math.Max(pair?.MinOrderSizeQuote ?? 0m, 0.00000001m);
        return new SellableNow(
            snapshot.SellableBaseBySymbol?.GetValueOrDefault(symbol) ?? 0m,
            snapshot.PositionQuoteBySymbol.GetValueOrDefault(symbol) >= minimum);
    }

    /// <summary>
    /// Cancels one open order the engine placed. Cancelling never adds risk, so it needs no gate, but orders the engine
    /// didn't place are left alone (reconciliation treats them as a reason to halt).
    /// </summary>
    public async Task<(CancelOutcome Outcome, string? ClientOrderId)> CancelOwnOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueOrderId);
        IReadOnlyList<ExchangeOrder> open;
        try
        {
            // The cached listing: a looping agent can't use up the rate limit that kill-switch cancels need.
            open = await GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException)
        {
            return (CancelOutcome.Failed, null);
        }

        var order = open.FirstOrDefault(o => o.IsOpen && string.Equals(o.VenueOrderId, venueOrderId, StringComparison.Ordinal));
        if (order is null)
            return (CancelOutcome.NotFound, null);
        bool ours;
        lock (_pending)
            ours = _usedClientOrderIds.Contains(order.ClientOrderId);
        if (!ours)
            return (CancelOutcome.NotOwn, null);

        if (!_lease.IsHeld)
            return (CancelOutcome.Failed, order.ClientOrderId);

        try
        {
            var cancelled = await _exchange.CancelOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false);
            ClearCaches();
            return (cancelled ? CancelOutcome.Cancelled : CancelOutcome.NotFound, order.ClientOrderId);
        }
        catch (ExchangeException)
        {
            return (CancelOutcome.Failed, order.ClientOrderId);
        }
    }

    /// <summary>
    /// Cancels every open order the engine placed on <paramref name="symbol"/>, read fresh from the exchange: before a protective
    /// exit, so a resting sell (a target, a trim, an agent's) frees the coins it holds, and a resting buy doesn't add to a
    /// falling position. Orders the engine didn't place are left alone. Returns how many were cancelled.
    /// </summary>
    /// <exception cref="ExchangeException">Open orders couldn't be listed.</exception>
    public async Task<int> CancelOwnOrdersOnAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = MarketDataPoller.NormaliseSymbol(symbol);
        var open = await _exchange.GetOpenOrdersAsync(symbol, cancellationToken).ConfigureAwait(false);
        var cancelled = 0;
        foreach (var order in open.Where(o => o.IsOpen && MarketDataPoller.NormaliseSymbol(o.Symbol) == symbol))
        {
            bool ours;
            lock (_pending)
                ours = _usedClientOrderIds.Contains(order.ClientOrderId);
            if (!ours || !_lease.IsHeld)
                continue;
            try
            {
                if (await _exchange.CancelOrderAsync(order.VenueOrderId, cancellationToken).ConfigureAwait(false))
                    cancelled++;
            }
            catch (ExchangeException ex)
            {
                LogCancelFailed(_logger, order.VenueOrderId, ex.Message);
            }
        }

        if (cancelled > 0)
            ClearCaches();
        return cancelled;
    }

    /// <summary>Appends to the journal if there is one. A journal failure is logged, never allowed to stop a safety action.</summary>
    private async Task<bool> RecordAsync(string kind, string actor, string summary, IReadOnlyDictionary<string, string> data, string? runId = null, string? spec = null)
    {
        if (_journal is null)
            return true;
        try
        {
            var now = _time.GetUtcNow();
            await _journal.AppendAsync(
                new JournalEntry(JournalIds.New(now, kind), now, kind, actor, runId, spec, summary, data),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateWriteFailed(_logger, "journal: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Engages the kill switch (persisted first, so new orders stop at once), waits for any placement in flight,
    /// then cancels every open order. Cancellation failures are reported; the switch stays engaged regardless.
    /// </summary>
    public async Task<KillSwitchResult> EngageKillSwitchAsync(string reason, string by, CancellationToken cancellationToken)
    {
        reason = Truncate(reason);
        by = Truncate(by);
        bool newly;
        try
        {
            newly = _killSwitch.Engage(reason, by);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Engaged in memory regardless (see FileEngineStateStore); keep going and cancel orders.
            newly = true;
            LogStateWriteFailed(_logger, ex.Message);
        }

        LogKillSwitch(_logger, by, reason);
        if (!newly)
            await NoteAlsoTriggeredAsync(reason, by).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var notCancelled = new List<string>();
            var cancelled = 0;
            IReadOnlyList<ExchangeOrder> open;
            try
            {
                open = await _exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
            }
            catch (ExchangeException ex)
            {
                LogCancelFailed(_logger, "(listing open orders)", ex.Message);
                if (newly)
                    await RecordAsync("kill-switch", by, $"Engaged: {reason}", new Dictionary<string, string> { ["action"] = "engaged", ["reason"] = reason, ["cancelled"] = "unknown" }).ConfigureAwait(false);
                return new KillSwitchResult(newly, 0, ["(open orders could not be listed)"]);
            }

            // Orders sent moments ago may not be listed yet: cancel those by their venue ID too.
            var targets = open.Where(o => o.IsOpen).Select(o => o.VenueOrderId).ToHashSet(StringComparer.Ordinal);
            lock (_pending)
            {
                foreach (var pending in _pending.Values)
                {
                    if (pending.VenueOrderId is { } venueId)
                        targets.Add(venueId);
                    else if (pending.Uncertain)
                        notCancelled.Add($"uncertain:{pending.Order.ClientOrderId}"); // unknown venue ID: reconcile by hand
                }
            }

            foreach (var venueOrderId in targets)
            {
                if (!_lease.IsHeld)
                {
                    notCancelled.Add(venueOrderId); // another engine may own these now
                    continue;
                }

                try
                {
                    // False = the exchange doesn't know it (already filled or cancelled): nothing left to stop.
                    if (await _exchange.CancelOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false))
                        cancelled++;
                }
                catch (ExchangeException ex)
                {
                    LogCancelFailed(_logger, venueOrderId, ex.Message);
                    notCancelled.Add(venueOrderId);
                }
            }

            var result = new KillSwitchResult(newly, cancelled, notCancelled);
            if (newly)
            {
                await RecordAsync("kill-switch", by, $"Engaged: {reason}", new Dictionary<string, string>
                {
                    ["action"] = "engaged",
                    ["reason"] = reason,
                    ["cancelled"] = cancelled.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["notCancelled"] = string.Join(",", notCancelled),
                }).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A check that would halt trading found a problem while trading already was: recorded beside the halt and journaled
    /// (once per cause and reason), so the dashboard's Resume can't release a halt the owner hasn't seen (ADR-0018). Cancels nothing.
    /// </summary>
    internal async Task NoteAlsoTriggeredAsync(string reason, string by)
    {
        reason = Truncate(reason);
        by = Truncate(by);
        bool noted;
        try
        {
            noted = _killSwitch.NoteAlso(reason, by);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            noted = true; // kept in memory until saved (KillSwitch.State); the journal still gets it
            LogStateWriteFailed(_logger, ex.Message);
        }

        if (noted)
            await RecordAsync("kill-switch", by, $"Also triggered: {reason}", new Dictionary<string, string> { ["action"] = "also-triggered", ["reason"] = reason }).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels every open buy (listed, or sent and not listed yet), leaving sells working. Used at the drawdown limit,
    /// where the gate already refuses new buys but risk-reducing sells must stay possible. Returns how many were cancelled.
    /// </summary>
    public async Task<int> CancelOpenBuysAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var open = await _exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
            var targets = open.Where(o => o.IsOpen && o.Side == OrderSide.Buy).Select(o => o.VenueOrderId).ToHashSet(StringComparer.Ordinal);
            lock (_pending)
            {
                foreach (var p in _pending.Values.Where(p => p.Order.Side == OrderSide.Buy && p.VenueOrderId is not null))
                    targets.Add(p.VenueOrderId!);
            }

            var cancelled = 0;
            foreach (var venueOrderId in targets)
            {
                if (!_lease.IsHeld)
                    break; // another engine may own these now

                try
                {
                    if (await _exchange.CancelOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false))
                        cancelled++;
                }
                catch (ExchangeException ex)
                {
                    LogCancelFailed(_logger, venueOrderId, ex.Message);
                }
            }

            return cancelled;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Owner only: resumes trading. Never exposed as an agent tool.</summary>
    public async Task ReleaseKillSwitchAsync(string by)
    {
        by = Truncate(by);
        Interlocked.Exchange(ref _consecutiveRejects, 0); // a fresh start, not one rejection away from halting again
        var wasEngaged = _killSwitch.IsEngaged;
        _killSwitch.Release(by);
        LogKillSwitchReleased(_logger, by);
        if (wasEngaged)
            await RecordAsync("kill-switch", by, "Released", new Dictionary<string, string> { ["action"] = "released" }).ConfigureAwait(false);
    }

    /// <summary>Size rounds down to the base step; price rounds to the passive side (buys down, sells up) of the quote step.</summary>
    public static OrderIntent RoundToSteps(OrderIntent intent, PairInfo pair)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(pair);
        var size = Steps.Floor(intent.BaseSize, pair.BaseStep);
        var price = intent.Side == OrderSide.Buy ? Steps.Floor(intent.LimitPrice, pair.QuoteStep) : Steps.Ceiling(intent.LimitPrice, pair.QuoteStep);
        return intent with { BaseSize = size, LimitPrice = price };
    }

    private async Task<ExecutionResult> SendAsync(
        ApprovedOrder order, RiskVerdict verdict, bool postOnly, OrderContext? context, bool holdsPosition,
        Func<CancellationToken, Task>? beforeSend, CancellationToken cancellationToken)
    {
        // The kill switch may have engaged while the portfolio was being read; it flips before taking the lock.
        if (_killSwitch.IsEngaged && order.Purpose != OrderPurpose.ProtectiveExit)
            return Reject("kill-switch", "The kill switch is engaged; no orders are allowed.");

        // Another engine of this mode may be trading, or this one couldn't renew its lease: it must not act (ADR-0013).
        if (!_lease.IsHeld)
            return Reject(LeaseRule, LeaseReason);

        // The caller's own pacing, taken under the lock just before the send: the stop-loss sells of many coins leave the lock
        // one at a time, so their pace holds at the exchange and not only at the door. Nothing is recorded yet if it is cancelled.
        if (beforeSend is not null)
            await beforeSend(cancellationToken).ConfigureAwait(false);

        // Record the order and its plan before sending: if the engine dies mid-send, the order is still recognised as ours
        // after a restart, and the position it opens still has its stop and target.
        var sent = new SentOrder(
            order.ClientOrderId, order.Symbol, SideName(order.Side), order.BaseSize, order.LimitPrice, _time.GetUtcNow(),
            context?.Source ?? "engine", StopLoss: context?.StopLoss, TakeProfit: context?.TakeProfit, RunId: context?.RunId, Strategy: context?.Strategy);
        try
        {
            _store.Update(s => s.WithSent(sent, holdsPosition));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateWriteFailed(_logger, ex.Message);
            // A stop-loss sell can't rest, so a restart has no working order to recognise: a database outage mustn't stop it.
            if (order.Purpose != OrderPurpose.ProtectiveExit)
                return Reject("state-unavailable", "The engine could not record the order before sending it; nothing was sent.");
        }

        Interlocked.Exchange(ref _consecutiveRejects, 0);
        _sentNotionalSinceReconcile += order.NotionalQuote;
        if (order.Purpose == OrderPurpose.ProtectiveExit)
            _extraAllowanceSinceReconcile += order.NotionalQuote * _gate.Limits.EmergencySlippageFraction; // may fill that far below the mid
        var sentAt = _time.GetUtcNow();
        _sent.Enqueue((sentAt, order.Purpose == OrderPurpose.ProtectiveExit)); // counts toward the order rate even if it fails
        _recentOrders[order.Symbol] = sentAt; // its order book is polled from now on
        lock (_pending)
        {
            _pending[order.ClientOrderId] = new PendingPlacement(order, sentAt, Uncertain: false);
            _usedClientOrderIds.Add(order.ClientOrderId);
        }

        try
        {
            var placed = await _exchange.PlaceLimitOrderAsync(order, postOnly, cancellationToken).ConfigureAwait(false);
            if (postOnly && await IsRejectedAsync(placed, cancellationToken).ConfigureAwait(false))
            {
                // The exchange records a post-only order that would cross, then rejects it: nothing is working.
                lock (_pending)
                    _pending.Remove(order.ClientOrderId);
                TryUpdateState(s => s.WithEntryAbandoned(order.ClientOrderId));
                return new ExecutionResult(ExecutionStatus.Failed, "post-only-would-cross", "The order would have filled at once, so the post-only order was rejected.", order, placed);
            }

            return Confirmed(order, verdict, placed, sentAt);
        }
        catch (OrderPlacementUncertainException)
        {
            ClearCaches();
            var found = await FindByClientOrderIdAsync(order).ConfigureAwait(false);
            if (found is not null)
                return Confirmed(order, verdict, found, sentAt);

            lock (_pending)
                _pending[order.ClientOrderId] = new PendingPlacement(order, sentAt, Uncertain: true);
            LogUncertain(_logger, order.ToString());
            return new ExecutionResult(
                ExecutionStatus.Uncertain,
                "placement-uncertain",
                "The exchange may or may not have the order; it counts as open until reconciliation finds out.",
                order);
        }
        catch (ExchangeException ex)
        {
            ClearCaches();
            lock (_pending)
            {
                _pending.Remove(order.ClientOrderId);
                _usedClientOrderIds.Remove(order.ClientOrderId); // refused, so the same intent may be retried
            }

            ForgetKnown([order.ClientOrderId]);
            TryUpdateState(s => s.WithEntryAbandoned(order.ClientOrderId));

            LogFailed(_logger, order.ToString(), ex.ErrorId ?? ex.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "error");
            return new ExecutionResult(ExecutionStatus.Failed, "exchange-refused", "The exchange refused the order.", order); // its own error text stays in the log
        }
    }

    /// <summary>After any placement attempt, so a listing taken while it was in flight isn't served for the next 10 seconds.</summary>
    private void ClearCaches()
    {
        _openOrdersCache = null;
        _portfolioCache = null;
    }

    private async Task<bool> IsRejectedAsync(PlacedOrder placed, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _exchange.GetOrderAsync(placed.VenueOrderId, cancellationToken).ConfigureAwait(false);
            return status?.Status == OrderStatus.Rejected;
        }
        catch (ExchangeException)
        {
            return false; // unknown: treat as working; reconciliation settles it
        }
    }

    private ExecutionResult Confirmed(ApprovedOrder order, RiskVerdict verdict, PlacedOrder placed, DateTimeOffset sentAt)
    {
        ClearCaches();
        lock (_pending)
        {
            _pending[order.ClientOrderId] = new PendingPlacement(order, sentAt, Uncertain: false, placed.VenueOrderId);
            _tracked[order.ClientOrderId] = new TrackedOrder(order.ToIntent(), placed.VenueOrderId);
        }

        TryUpdateState(s => s.WithVenueOrderId(order.ClientOrderId, placed.VenueOrderId));
        LogPlaced(_logger, order.ToString(), placed.VenueOrderId);
        return new ExecutionResult(ExecutionStatus.Placed, verdict.Rule, verdict.Reason, order, placed);
    }

    /// <summary>Looks for the order among open orders a few times. Uses its own timeout, not the caller's token,
    /// because a caller that gave up is exactly when we must find out.</summary>
    private async Task<PlacedOrder?> FindByClientOrderIdAsync(ApprovedOrder order)
    {
        // An immediate-or-cancel order is never open by the time the exchange has answered, so there is nothing to find here,
        // and 7 seconds under the placement lock would hold up every other coin's stop-loss sell. Its fill shows in the balance
        // the next try reads; reconciliation drops the uncertain placement later.
        if (order.TimeInForce == TimeInForce.ImmediateOrCancel)
            return null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        foreach (var wait in ReconcileDelays)
        {
            try
            {
                await _delay(wait, timeout.Token).ConfigureAwait(false);
                var open = await _exchange.GetOpenOrdersAsync(order.Symbol, timeout.Token).ConfigureAwait(false);
                var match = open.FirstOrDefault(o => string.Equals(o.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal));
                if (match is not null)
                    return new PlacedOrder(match.VenueOrderId, match.ClientOrderId);
            }
            catch (ExchangeException)
            {
                // Try again; the next attempt or the reconciler will settle it.
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches the coin's order book before an order is priced when the latest quote is a ticker (no sizes) or older than
    /// <see cref="_freshBookWithin"/>: the gate's price band and a paper fill then rest on a book seconds old. A fetch that
    /// fails leaves the latest quote in place, and the gate's own freshness rule decides.
    /// </summary>
    private async Task RefreshBookIfNeededAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_refreshQuote is null)
            return;
        var latest = _market.Quote(symbol);
        if (latest is not null && (latest.BidSize > 0m || latest.AskSize > 0m) && now - latest.AsOf <= _freshBookWithin)
            return;
        try
        {
            await _refreshQuote(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
        {
            LogBookRefreshFailed(_logger, symbol, ex.GetType().Name);
        }
    }

    private List<PendingPlacement> PendingSnapshot()
    {
        var now = _time.GetUtcNow();
        lock (_pending)
        {
            foreach (var expired in _pending.Values.Where(p => !p.Uncertain && now - p.SentAt > PendingWindow).ToList())
                _pending.Remove(expired.Order.ClientOrderId);
            return _pending.Values.ToList();
        }
    }

    private void PruneSent(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek().At >= TimeSpan.FromMinutes(1))
            _sent.Dequeue();
    }

    private static ExecutionResult Reject(string rule, string reason) => new(ExecutionStatus.Rejected, rule, reason);

    /// <summary>The engine's reason code when it doesn't hold the engine lease (ADR-0013).</summary>
    public const string LeaseRule = "engine-lease";

    private const string LeaseReason = "This engine doesn't hold the engine lease for its trading mode (another engine may be trading); nothing was sent.";

    /// <summary>Drops finished orders from the persisted known set. A failed write only leaves extra IDs, which is safe.</summary>
    private void ForgetKnown(IReadOnlyCollection<string> clientOrderIds)
    {
        if (clientOrderIds.Count > 0)
            TryUpdateState(s => s.WithoutOrders(clientOrderIds));
    }

    /// <summary>Bookkeeping updates to the engine state: a failure is logged, and the next reconciliation repairs it.</summary>
    private void TryUpdateState(Func<EngineState, EngineState> change)
    {
        try
        {
            _store.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateWriteFailed(_logger, ex.Message);
        }
    }

    private static string Truncate(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.Length <= 200 ? text : text[..200];
    }


    [LoggerMessage(Level = LogLevel.Information, Message = "Order {ClientOrderId} rejected by {Rule}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string clientOrderId, string rule, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Placed {Order} as {VenueOrderId}")]
    private static partial void LogPlaced(ILogger logger, string order, string venueOrderId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Placement outcome unknown for {Order}; held until reconciled")]
    private static partial void LogUncertain(ILogger logger, string order);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exchange refused {Order}: {Error}")]
    private static partial void LogFailed(ILogger logger, string order, string error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Kill switch engaged by {By}: {Reason}")]
    private static partial void LogKillSwitch(ILogger logger, string by, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kill switch released by {By}")]
    private static partial void LogKillSwitchReleased(ILogger logger, string by);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The engine state file could not be written: {Detail}")]
    private static partial void LogStateWriteFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Portfolio could not be valued: {Detail}")]
    private static partial void LogSampleFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not cancel {VenueOrderId}: {Detail}")]
    private static partial void LogCancelFailed(ILogger logger, string venueOrderId, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order book for {Symbol} could not be refreshed before pricing ({Error}); the latest quote is used")]
    private static partial void LogBookRefreshFailed(ILogger logger, string symbol, string error);
}
