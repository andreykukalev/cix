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

    private Dictionary<string, decimal>? _lastHoldings;
    private HashSet<string> _missingOnce = new(StringComparer.Ordinal);

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
            lock (_pending)
            {
                foreach (var p in _pending.Values.Where(p => p.Uncertain).ToList())
                {
                    var match = open.FirstOrDefault(o => string.Equals(o.ClientOrderId, p.Order.ClientOrderId, StringComparison.Ordinal));
                    if (match is not null)
                    {
                        _pending[p.Order.ClientOrderId] = p with { Uncertain = false, VenueOrderId = match.VenueOrderId };
                        resolved++;
                    }
                    else if (now - p.SentAt >= _safety.UncertainTimeout)
                    {
                        // Not listed long after sending: it never arrived, or it already filled (balances show that).
                        _pending.Remove(p.Order.ClientOrderId);
                        dropped++;
                        LogUncertainDropped(_logger, p.Order.ToString());
                    }
                }

                pendingIds = _pending.Keys.ToHashSet(StringComparer.Ordinal);
            }

            var known = _store.Current.Known.ToHashSet(StringComparer.Ordinal);
            var unknown = open
                .Where(o => o.IsOpen && !known.Contains(o.ClientOrderId) && !pendingIds.Contains(o.ClientOrderId))
                .Select(o => o.VenueOrderId)
                .ToList();
            // Forget an ID only when two reconciliations in a row don't see it, so an order sent just before a restart
            // has time to be listed before it could be mistaken for someone else's.
            var missing = known.Where(id => !openIds.Contains(id) && !pendingIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
            ForgetKnown(missing.Where(_missingOnce.Contains).ToList());
            missing.ExceptWith(_missingOnce);
            _missingOnce = missing;

            var holdings = balances
                .GroupBy(b => b.Currency.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(b => b.Total), StringComparer.OrdinalIgnoreCase);
            decimal? unexplained = null;
            if (_lastHoldings is not null)
            {
                try
                {
                    unexplained = await _portfolio.ValueOfChangeAsync(_lastHoldings, holdings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
                {
                    LogReconcileFailed(_logger, "pair rules unavailable for the holdings check");
                }
            }

            _lastHoldings = holdings;

            string? reason = null;
            if (unknown.Count > 0)
                reason = $"Open orders the engine didn't send: {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? ", ..." : "")}.";
            else if (unexplained is { } change && Math.Abs(change) > _safety.BalanceToleranceQuote)
                reason = $"Holdings changed by {change:0.##} in quote currency without matching fills (deposit, withdrawal or outside trading?).";

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation skipped: {Detail}")]
    private static partial void LogReconcileFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncertain placement {Order} still not listed; taken as not open")]
    private static partial void LogUncertainDropped(ILogger logger, string order);
}
