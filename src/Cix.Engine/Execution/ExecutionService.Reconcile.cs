using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Engine.Execution;

/// <summary>What one reconciliation found.</summary>
/// <param name="UnknownOrders">Venue IDs of open orders the engine didn't send.</param>
/// <param name="UnexplainedChangeQuote">Change in holdings value that fills can't explain; null on the first run or without prices.</param>
/// <param name="KillSwitchReason">Why the kill switch was engaged, or null.</param>
public sealed record ReconcileReport(
    IReadOnlyList<string> UnknownOrders,
    int ResolvedUncertain,
    int DroppedUncertain,
    decimal? UnexplainedChangeQuote,
    string? KillSwitchReason)
{
    public static readonly ReconcileReport Skipped = new([], 0, 0, null, null);
}

public sealed partial class ExecutionService
{
    public const string ReconcilerBy = "reconciler";

    private Dictionary<string, DateTimeOffset> _missingSince = new(StringComparer.Ordinal);
    private HashSet<string> _uncertainMissedOnce = new(StringComparer.Ordinal);
    private decimal _sentNotionalSinceReconcile;

    /// <summary>
    /// Compares the exchange with what the engine expects. An open order the engine didn't send, or a change in holdings
    /// that fills can't explain (a deposit, withdrawal or trade from elsewhere), engages the kill switch: the account is
    /// no longer the engine's alone. Also settles uncertain placements and forgets finished orders.
    /// </summary>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        ReconcileReport report;
        HashSet<string>? openForFills = null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ExchangeOrder> open;
            IReadOnlyList<Balance> balances;
            try
            {
                open = await _exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
                balances = await _exchange.GetBalancesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
            {
                LogReconcileFailed(_logger, ex.Message);
                return ReconcileReport.Skipped;
            }

            var now = _time.GetUtcNow();
            PendingSnapshot(); // drops confirmed orders past the pending window
            var openIds = open.Where(o => o.IsOpen).Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
            var resolved = 0;
            var dropped = 0;
            HashSet<string> pendingIds;
            var missedPastTimeout = new HashSet<string>(StringComparer.Ordinal);
            var droppedIds = new List<string>();
            lock (_pending)
            {
                foreach (var p in _pending.Values.Where(p => p.Uncertain).ToList())
                {
                    var id = p.Order.ClientOrderId;
                    var match = open.FirstOrDefault(o => string.Equals(o.ClientOrderId, id, StringComparison.Ordinal));
                    if (match is not null)
                    {
                        _pending[id] = p with { Uncertain = false, VenueOrderId = match.VenueOrderId };
                        _tracked[id] = new TrackedOrder(p.Order.ToIntent(), match.VenueOrderId); // so its fill is journaled
                        resolved++;
                    }
                    else if (now - p.SentAt >= _safety.UncertainTimeout)
                    {
                        // Past the timeout and missing from two listings in a row: it never arrived, or it already
                        // filled (the balances show that). One listing alone could just be lagging.
                        if (_uncertainMissedOnce.Contains(id))
                        {
                            _pending.Remove(id);
                            droppedIds.Add(id);
                            dropped++;
                            LogUncertainDropped(_logger, p.Order.ToString());
                        }
                        else
                        {
                            missedPastTimeout.Add(id);
                        }
                    }
                }

                pendingIds = _pending.Keys.ToHashSet(StringComparer.Ordinal);
            }

            _uncertainMissedOnce = missedPastTimeout;
            foreach (var id in droppedIds)
                TryUpdateState(s => s.WithEntryAbandoned(id)); // never arrived: its plan (or pending levels) go with it

            var known = _store.Current.Known.ToHashSet(StringComparer.Ordinal);
            var unknown = open
                .Where(o => o.IsOpen && !known.Contains(o.ClientOrderId) && !pendingIds.Contains(o.ClientOrderId))
                .Select(o => o.VenueOrderId)
                .ToList();
            // Forget an ID only once it has been missing for a full reconcile interval (seen missing at least twice), so an
            // order sent just before a restart has time to be listed before it could be mistaken for someone else's.
            var missing = known.Where(id => !openIds.Contains(id) && !pendingIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
            var missingSince = missing.ToDictionary(id => id, id => _missingSince.GetValueOrDefault(id, now), StringComparer.Ordinal);
            HashSet<string> stillTracked;
            lock (_pending)
                stillTracked = _tracked.Keys.ToHashSet(StringComparer.Ordinal); // kept until their fill is journaled, with its details
            var forget = missingSince.Where(m => m.Value < now && now - m.Value >= _safety.ReconcileInterval && !stillTracked.Contains(m.Key)).Select(m => m.Key).ToList();
            ForgetKnown(forget);
            foreach (var id in forget)
                missingSince.Remove(id);
            _missingSince = missingSince;

            AdoptKnownOpenOrders(open, known);
            openForFills = openIds;

            // Orders whose placement outcome was unknown (or sent just before a restart) get their exchange ID once listed.
            foreach (var listed in open.Where(o => o.IsOpen && _store.Current.SentOrders.Any(s => s.ClientOrderId == o.ClientOrderId && s.VenueOrderId is null)))
                TryUpdateState(s => s.WithVenueOrderId(listed.ClientOrderId, listed.VenueOrderId));

            var holdings = balances
                .GroupBy(b => b.Currency.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(b => b.Total), StringComparer.OrdinalIgnoreCase);
            var engineOrdersNotional = open
                .Where(o => o.IsOpen && (known.Contains(o.ClientOrderId) || pendingIds.Contains(o.ClientOrderId)))
                .Sum(o => o.Quantity * (o.Price ?? 0m));
            var (unexplained, allowance) = await CheckHoldingsAsync(holdings, engineOrdersNotional, cancellationToken).ConfigureAwait(false);
            await PrunePositionPlansAsync(holdings, openIds, pendingIds, now, cancellationToken).ConfigureAwait(false);

            string? reason = null;
            if (unknown.Count > 0)
                reason = $"Open orders the engine didn't send: {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? ", ..." : "")}.";
            else if (unexplained is { } change && Math.Abs(change) > allowance)
                reason = $"Holdings changed by {change:0.##} in quote currency, more than the engine's fills can explain ({allowance:0.##}): deposit, withdrawal or outside trading?";

            report = new ReconcileReport(unknown, resolved, dropped, unexplained, reason);
        }
        finally
        {
            _lock.Release();
        }

        if (report.KillSwitchReason is not null && !_killSwitch.IsEngaged)
            await EngageKillSwitchAsync(report.KillSwitchReason, ReconcilerBy, cancellationToken).ConfigureAwait(false);

        // Bookkeeping last, outside the lock, so it never delays the checks or the kill switch.
        if (openForFills is not null)
            await JournalFinishedOrdersAsync(openForFills, cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Values the change since the persisted baseline. The baseline moves forward only after a successful comparison,
    /// so a change during a stale-price gap or a restart is still compared later rather than absorbed.
    /// </summary>
    private async Task<(decimal? Unexplained, decimal Allowance)> CheckHoldingsAsync(
        Dictionary<string, decimal> holdings,
        decimal engineOrdersNotional,
        CancellationToken cancellationToken)
    {
        var baseline = _store.Current.Holdings;
        var allowance = _safety.BalanceToleranceQuote
            + ((baseline?.EngineOrdersNotional ?? 0m) + _sentNotionalSinceReconcile) * SafetyOptions.FillAllowanceFraction;
        decimal? unexplained = null;
        if (baseline is not null)
        {
            try
            {
                unexplained = await _portfolio.ValueOfChangeAsync(baseline.Holdings, holdings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
            {
                LogReconcileFailed(_logger, "pair rules unavailable for the holdings check");
            }

            if (unexplained is null)
                return (null, allowance); // keep the old baseline until a comparison succeeds
        }

        try
        {
            _store.Update(s => s with { Holdings = new HoldingsBaseline(holdings, engineOrdersNotional) });
            _sentNotionalSinceReconcile = 0m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateWriteFailed(_logger, ex.Message);
        }

        return (unexplained, allowance);
    }

    /// <summary>
    /// Orders this process placed that are no longer open: looks each up and journals how it ended (filled size, value,
    /// fee). One still invisible to the lookup is checked again next time.
    /// </summary>
    public const int MaxFillLookups = 5;

    /// <summary>
    /// After a restart, the engine's own orders are known by client order ID only: when one shows up open, start tracking it
    /// so its fill is journaled too. (An order that filled while the engine was down is seen only in the balances.)
    /// </summary>
    private void AdoptKnownOpenOrders(IReadOnlyList<ExchangeOrder> open, HashSet<string> known)
    {
        lock (_pending)
        {
            foreach (var o in open.Where(o => o.IsOpen && known.Contains(o.ClientOrderId) && !_tracked.ContainsKey(o.ClientOrderId)))
            {
                var intent = new OrderIntent(MarketData.MarketDataPoller.NormaliseSymbol(o.Symbol), o.Side, o.Quantity, o.Price ?? 0m, o.ClientOrderId);
                _tracked[o.ClientOrderId] = new TrackedOrder(intent, o.VenueOrderId);
            }
        }
    }

    /// <summary>
    /// Removes plans whose position is gone: less than the exchange minimum held, its entry buy no longer working, and old enough
    /// that the buy can't still be on its way. The holdings decide, so a plan survives any restart while its position exists.
    /// </summary>
    private async Task PrunePositionPlansAsync(
        Dictionary<string, decimal> holdings, HashSet<string> openIds, HashSet<string> pendingIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var gone = new List<string>();
        foreach (var plan in _store.Current.Plans.Values)
        {
            if (openIds.Contains(plan.EntryClientOrderId) || pendingIds.Contains(plan.EntryClientOrderId) || now - plan.Since < PendingWindow
                || (plan.PendingClientOrderId is { } further && (openIds.Contains(further) || pendingIds.Contains(further))))
                continue;
            PairInfo? pair;
            try
            {
                pair = await _pairs.GetAsync(plan.Symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
            {
                return; // can't tell what is sellable now: keep every plan
            }

            var held = pair is null ? 0m : holdings.GetValueOrDefault(pair.Base);
            if (pair is null || held <= 0m || held < pair.MinOrderSize)
                gone.Add(plan.Symbol);
        }

        if (gone.Count > 0)
            TryUpdateState(s => s.WithoutPlans(gone));
    }

    /// <summary>How far back a finished order's journal entries are looked for, so a restart never journals a fill twice.</summary>
    public const int FillLookbackDays = 7;

    private async Task<bool> AlreadyJournaledAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        if (_journal is null)
            return false;
        var from = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(-FillLookbackDays);
        var entries = await _journal.QueryAsync(new Journal.JournalQuery(From: from, ClientOrderId: clientOrderId, Limit: 50), cancellationToken).ConfigureAwait(false);
        return entries.Any(e => e.Kind is "fill" or "order-closed");
    }

    private async Task JournalFinishedOrdersAsync(HashSet<string> openIds, CancellationToken cancellationToken)
    {
        List<(string ClientOrderId, TrackedOrder Tracked)> candidates;
        lock (_pending)
            candidates = _tracked.Where(t => !openIds.Contains(t.Key)).Select(t => (t.Key, t.Value)).ToList();

        foreach (var (clientOrderId, tracked) in candidates)
        {
            var intent = tracked.Order;
            var venueOrderId = tracked.VenueOrderId;
            ExchangeOrder? order;
            try
            {
                order = await _exchange.GetOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (ExchangeException)
            {
                order = null;
            }

            if (order is { IsOpen: true })
                continue; // still working, just not listed yet: it will be

            if (order is null)
            {
                // Not visible to the lookup: try again next time, but not forever.
                lock (_pending)
                {
                    if (tracked.Lookups + 1 >= MaxFillLookups)
                        _tracked.Remove(clientOrderId);
                    else
                        _tracked[clientOrderId] = tracked with { Lookups = tracked.Lookups + 1 };
                }

                continue;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var average = order.FilledQuantity > 0 ? order.FilledAmountQuote / order.FilledQuantity : 0m;
            var sent = _store.Current.SentOrders.FirstOrDefault(o => o.ClientOrderId == clientOrderId);
            var recorded = false;
            try
            {
                // An order rebuilt after a restart may have had its fill journaled before it: journal how it ended once only.
                if (tracked.Restored)
                    recorded = await AlreadyJournaledAsync(clientOrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogStateWriteFailed(_logger, "journal read: " + ex.GetType().Name);
                continue; // try again next time rather than risk a duplicate
            }

            var data = new Dictionary<string, string>
                {
                    ["clientOrderId"] = clientOrderId,
                    ["orderId"] = venueOrderId,
                    ["symbol"] = intent.Symbol,
                    ["side"] = intent.Side.ToString().ToLowerInvariant(),
                    ["size"] = intent.BaseSize.ToString(inv),
                    ["limitPrice"] = intent.LimitPrice.ToString(inv),
                    ["filledSize"] = order.FilledQuantity.ToString(inv),
                    ["filledQuote"] = order.FilledAmountQuote.ToString(inv),
                    ["averagePrice"] = Math.Round(average, 8).ToString(inv),
                    ["fee"] = order.TotalFee.ToString(inv),
                    ["feeCurrency"] = order.FeeCurrency ?? string.Empty,
                    ["status"] = order.Status.ToString().ToLowerInvariant(),
                };
            if (sent is not null)
            {
                data["source"] = sent.Source;
                if (sent.StopLoss is { } stop)
                    data["stopLoss"] = stop.ToString(inv);
                if (sent.TakeProfit is { } target)
                    data["takeProfit"] = target.ToString(inv);
            }

            recorded = recorded || await RecordAsync(
                order.FilledQuantity > 0 ? "fill" : "order-closed",
                "engine",
                $"{intent.Side} {order.FilledQuantity.ToString(inv)} of {intent.BaseSize.ToString(inv)} {intent.Symbol} ({order.Status})",
                data,
                sent?.RunId,
                sent?.Strategy).ConfigureAwait(false);
            if (recorded)
            {
                lock (_pending)
                    _tracked.Remove(clientOrderId); // only once written: a failed write is retried next time
                if (intent.Side == OrderSide.Buy)
                {
                    TryUpdateState(s => order.FilledQuantity > 0
                        ? s.WithEntryFilled(intent.Symbol, clientOrderId, Math.Round(average, 8), _time.GetUtcNow())
                        : s.WithEntryAbandoned(clientOrderId));
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation skipped: {Detail}")]
    private static partial void LogReconcileFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncertain placement {Order} still not listed; taken as not open")]
    private static partial void LogUncertainDropped(ILogger logger, string order);
}
