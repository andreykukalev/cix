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
            lock (_pending)
            {
                foreach (var p in _pending.Values.Where(p => p.Uncertain).ToList())
                {
                    var id = p.Order.ClientOrderId;
                    var match = open.FirstOrDefault(o => string.Equals(o.ClientOrderId, id, StringComparison.Ordinal));
                    if (match is not null)
                    {
                        _pending[id] = p with { Uncertain = false, VenueOrderId = match.VenueOrderId };
                        resolved++;
                    }
                    else if (now - p.SentAt >= _safety.UncertainTimeout)
                    {
                        // Past the timeout and missing from two listings in a row: it never arrived, or it already
                        // filled (the balances show that). One listing alone could just be lagging.
                        if (_uncertainMissedOnce.Contains(id))
                        {
                            _pending.Remove(id);
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

            var known = _store.Current.Known.ToHashSet(StringComparer.Ordinal);
            var unknown = open
                .Where(o => o.IsOpen && !known.Contains(o.ClientOrderId) && !pendingIds.Contains(o.ClientOrderId))
                .Select(o => o.VenueOrderId)
                .ToList();
            // Forget an ID only once it has been missing for a full reconcile interval (seen missing at least twice), so an
            // order sent just before a restart has time to be listed before it could be mistaken for someone else's.
            var missing = known.Where(id => !openIds.Contains(id) && !pendingIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
            var missingSince = missing.ToDictionary(id => id, id => _missingSince.GetValueOrDefault(id, now), StringComparer.Ordinal);
            var forget = missingSince.Where(m => m.Value < now && now - m.Value >= _safety.ReconcileInterval).Select(m => m.Key).ToList();
            ForgetKnown(forget);
            foreach (var id in forget)
                missingSince.Remove(id);
            _missingSince = missingSince;

            await JournalFinishedOrdersAsync(openIds, cancellationToken).ConfigureAwait(false);

            var holdings = balances
                .GroupBy(b => b.Currency.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(b => b.Total), StringComparer.OrdinalIgnoreCase);
            var engineOrdersNotional = open
                .Where(o => o.IsOpen && (known.Contains(o.ClientOrderId) || pendingIds.Contains(o.ClientOrderId)))
                .Sum(o => o.Quantity * (o.Price ?? 0m));
            var (unexplained, allowance) = await CheckHoldingsAsync(holdings, engineOrdersNotional, cancellationToken).ConfigureAwait(false);

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
    private async Task JournalFinishedOrdersAsync(HashSet<string> openIds, CancellationToken cancellationToken)
    {
        List<(string ClientOrderId, OrderIntent Order, string VenueOrderId)> candidates;
        lock (_pending)
            candidates = _tracked.Where(t => !openIds.Contains(t.Key)).Select(t => (t.Key, t.Value.Order, t.Value.VenueOrderId)).ToList();

        foreach (var (clientOrderId, intent, venueOrderId) in candidates)
        {
            ExchangeOrder? order;
            try
            {
                order = await _exchange.GetOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (ExchangeException)
            {
                continue;
            }

            if (order is null || order.IsOpen)
                continue;

            lock (_pending)
                _tracked.Remove(clientOrderId);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var average = order.FilledQuantity > 0 ? order.FilledAmountQuote / order.FilledQuantity : 0m;
            await RecordAsync(
                order.FilledQuantity > 0 ? "fill" : "order-closed",
                "engine",
                $"{intent.Side} {order.FilledQuantity.ToString(inv)} of {intent.BaseSize.ToString(inv)} {intent.Symbol} ({order.Status})",
                new Dictionary<string, string>
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
                }).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation skipped: {Detail}")]
    private static partial void LogReconcileFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncertain placement {Order} still not listed; taken as not open")]
    private static partial void LogUncertainDropped(ILogger logger, string order);
}
