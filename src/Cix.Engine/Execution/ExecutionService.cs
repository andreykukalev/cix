using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

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

/// <summary>What engaging the kill switch did to open orders.</summary>
public sealed record KillSwitchResult(bool NewlyEngaged, int Cancelled, IReadOnlyList<string> NotCancelled);

/// <summary>
/// The only way the engine places orders: round to the pair's steps, value the portfolio, ask the <see cref="RiskGate"/>,
/// then send what it approved. Placements run one at a time, so two orders can't both pass the gate against the same
/// snapshot. An order whose outcome is unknown is looked up by client order ID, never resent.
/// </summary>
public sealed partial class ExecutionService
{
    public const string RejectStreakBy = "execution";

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
    private readonly TimeProvider _time;
    private readonly ILogger<ExecutionService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Dictionary<string, PendingPlacement> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedClientOrderIds = new(StringComparer.Ordinal);
    private int _consecutiveRejects;

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
        SafetyOptions? safety = null)
    {
        _exchange = exchange;
        _gate = gate;
        _portfolio = portfolio;
        _market = market;
        _pairs = pairs;
        _killSwitch = killSwitch;
        _store = store;
        _safety = safety ?? new SafetyOptions();
        _safety.Validate();
        _time = time;
        _logger = logger;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));

        // After a restart, orders sent before it are still ours: their IDs can't be reused and aren't "unknown".
        _usedClientOrderIds.UnionWith(store.Current.Known);
    }

    /// <summary>Orders sent whose outcome is unknown; they block reuse of their client order ID until reconciled.</summary>
    public IReadOnlyList<PendingPlacement> Unresolved
    {
        get
        {
            lock (_pending)
                return _pending.Values.Where(p => p.Uncertain).ToList();
        }
    }

    public async Task<ExecutionResult> PlaceAsync(OrderIntent intent, bool postOnly, CancellationToken cancellationToken)
    {
        var result = await PlaceLockedAsync(intent, postOnly, cancellationToken).ConfigureAwait(false);

        // Engaging takes the placement lock, so it runs after the placement released it.
        if (result.Status == ExecutionStatus.Rejected && Volatile.Read(ref _consecutiveRejects) >= _safety.MaxConsecutiveRejects && !_killSwitch.IsEngaged)
        {
            await EngageKillSwitchAsync(
                $"{_safety.MaxConsecutiveRejects} orders in a row were rejected by the risk gate (last: {result.Rule}).",
                RejectStreakBy,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ExecutionResult> PlaceLockedAsync(OrderIntent intent, bool postOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent = intent with { Symbol = MarketDataPoller.NormaliseSymbol(intent.Symbol) };

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_killSwitch.IsEngaged)
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

            var rounded = RoundToSteps(intent, pair);
            PortfolioSnapshot portfolio;
            try
            {
                portfolio = await _portfolio.GetSnapshotAsync(_sent.Count, PendingSnapshot(), cancellationToken).ConfigureAwait(false);
            }
            catch (PortfolioUnavailableException ex)
            {
                return Reject("portfolio-unavailable", ex.Message);
            }

            var verdict = _gate.Evaluate(rounded, portfolio, _market.Snapshot(rounded.Symbol), pair);
            if (verdict.Order is not { } order)
            {
                Interlocked.Increment(ref _consecutiveRejects);
                LogRejected(_logger, intent.ClientOrderId ?? string.Empty, verdict.Rule, verdict.Reason);
                return new ExecutionResult(ExecutionStatus.Rejected, verdict.Rule, verdict.Reason);
            }

            return await SendAsync(order, verdict, postOnly, cancellationToken).ConfigureAwait(false);
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
            return await _portfolio.GetSnapshotAsync(0, PendingSnapshot(), cancellationToken).ConfigureAwait(false);
        }
        catch (PortfolioUnavailableException ex)
        {
            LogSampleFailed(_logger, ex.Message);
            return null;
        }
    }

    /// <summary>Requests cancellation of one order. Cancelling never adds risk, so it needs no gate.</summary>
    public Task<bool> CancelAsync(string venueOrderId, CancellationToken cancellationToken) =>
        _exchange.CancelOrderAsync(venueOrderId, cancellationToken);

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

            return new KillSwitchResult(newly, cancelled, notCancelled);
        }
        finally
        {
            _lock.Release();
        }
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
    public void ReleaseKillSwitch(string by)
    {
        by = Truncate(by);
        Interlocked.Exchange(ref _consecutiveRejects, 0); // a fresh start, not one rejection away from halting again
        _killSwitch.Release(by);
        LogKillSwitchReleased(_logger, by);
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

    private async Task<ExecutionResult> SendAsync(ApprovedOrder order, RiskVerdict verdict, bool postOnly, CancellationToken cancellationToken)
    {
        // The kill switch may have engaged while the portfolio was being read; it flips before taking the lock.
        if (_killSwitch.IsEngaged)
            return Reject("kill-switch", "The kill switch is engaged; no orders are allowed.");

        // Record the ID before sending: if the engine dies mid-send, the order is still recognised as ours after a restart.
        try
        {
            _store.Update(s => s.Known.Contains(order.ClientOrderId, StringComparer.Ordinal)
                ? s
                : s with { KnownClientOrderIds = [.. s.Known, order.ClientOrderId] });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateWriteFailed(_logger, ex.Message);
            return Reject("state-unavailable", "The engine could not record the order before sending it; nothing was sent.");
        }

        Interlocked.Exchange(ref _consecutiveRejects, 0);
        _sentNotionalSinceReconcile += order.NotionalQuote;
        var sentAt = _time.GetUtcNow();
        _sent.Enqueue(sentAt); // counts toward the order rate even if it fails
        lock (_pending)
        {
            _pending[order.ClientOrderId] = new PendingPlacement(order, sentAt, Uncertain: false);
            _usedClientOrderIds.Add(order.ClientOrderId);
        }

        try
        {
            var placed = await _exchange.PlaceLimitOrderAsync(order, postOnly, cancellationToken).ConfigureAwait(false);
            return Confirmed(order, verdict, placed, sentAt);
        }
        catch (OrderPlacementUncertainException)
        {
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
            lock (_pending)
            {
                _pending.Remove(order.ClientOrderId);
                _usedClientOrderIds.Remove(order.ClientOrderId); // refused, so the same intent may be retried
            }

            ForgetKnown([order.ClientOrderId]);

            LogFailed(_logger, order.ToString(), ex.ErrorId ?? ex.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "error");
            return new ExecutionResult(ExecutionStatus.Failed, ex.ErrorId ?? "exchange-error", "The exchange refused the order.", order);
        }
    }

    private ExecutionResult Confirmed(ApprovedOrder order, RiskVerdict verdict, PlacedOrder placed, DateTimeOffset sentAt)
    {
        lock (_pending)
            _pending[order.ClientOrderId] = new PendingPlacement(order, sentAt, Uncertain: false, placed.VenueOrderId);
        LogPlaced(_logger, order.ToString(), placed.VenueOrderId);
        return new ExecutionResult(ExecutionStatus.Placed, verdict.Rule, verdict.Reason, order, placed);
    }

    /// <summary>Looks for the order among open orders a few times. Uses its own timeout, not the caller's token,
    /// because a caller that gave up is exactly when we must find out.</summary>
    private async Task<PlacedOrder?> FindByClientOrderIdAsync(ApprovedOrder order)
    {
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
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromMinutes(1))
            _sent.Dequeue();
    }

    private static ExecutionResult Reject(string rule, string reason) => new(ExecutionStatus.Rejected, rule, reason);

    /// <summary>Drops finished orders from the persisted known set. A failed write only leaves extra IDs, which is safe.</summary>
    private void ForgetKnown(IReadOnlyCollection<string> clientOrderIds)
    {
        if (clientOrderIds.Count == 0)
            return;
        try
        {
            _store.Update(s => s.Known.Any(clientOrderIds.Contains)
                ? s with { KnownClientOrderIds = s.Known.Where(id => !clientOrderIds.Contains(id)).ToList() }
                : s);
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
}
