using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>The portfolio can't be valued safely (no balances, or a held asset has no fresh price).</summary>
public sealed class PortfolioUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An order the engine sent that may not show in open orders yet, or whose placement outcome is unknown.</summary>
public sealed record PendingPlacement(ApprovedOrder Order, DateTimeOffset SentAt, bool Uncertain);

/// <summary>
/// Builds the risk gate's <see cref="PortfolioSnapshot"/> from exchange balances, open orders and current prices, so it
/// recovers the true state after any restart. Tracks peak equity and the equity at 00:00 UTC in the engine state store.
/// </summary>
/// <remarks>
/// Assumes a dedicated account: equity is the quote balance plus the base balances of the allowed symbols, all of which
/// must share one quote currency. Exposure is positions plus open and just-sent buy orders.
/// </remarks>
public sealed class PortfolioService(
    IExchangeClient exchange,
    PairCatalog pairs,
    MarketState market,
    KillSwitch killSwitch,
    IEngineStateStore store,
    RiskLimits limits,
    TimeProvider time)
{
    public async Task<PortfolioSnapshot> GetSnapshotAsync(
        int ordersInLastMinute,
        IReadOnlyCollection<PendingPlacement> pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var symbolPairs = new List<PairInfo>();
        foreach (var symbol in limits.AllowedSymbols.Order(StringComparer.Ordinal))
        {
            symbolPairs.Add(await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false)
                ?? throw new PortfolioUnavailableException($"The exchange doesn't list {symbol}."));
        }

        var quoteCurrency = symbolPairs.Select(p => p.Quote).Distinct(StringComparer.OrdinalIgnoreCase).ToList() switch
        {
            [var single] => single,
            var several => throw new PortfolioUnavailableException(
                $"Allowed symbols must share one quote currency; found {string.Join(", ", several)}."),
        };

        IReadOnlyList<Balance> balances;
        IReadOnlyList<ExchangeOrder> openOrders;
        try
        {
            balances = await exchange.GetBalancesAsync(cancellationToken).ConfigureAwait(false);
            openOrders = await exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            throw new PortfolioUnavailableException("Balances or open orders could not be read from the exchange.", ex);
        }

        var now = time.GetUtcNow();
        var positions = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var pair in symbolPairs)
        {
            var held = TotalOf(balances, pair.Base);
            if (held <= 0)
            {
                positions[pair.Symbol] = 0m;
                continue;
            }

            var quote = market.Quote(pair.Symbol);
            if (quote is null || now - quote.AsOf > HardCeilings.MaxMarketDataAge)
                throw new PortfolioUnavailableException($"{pair.Base} is held but {pair.Symbol} has no fresh price.");

            positions[pair.Symbol] = held * quote.Mid;
        }

        var allowed = symbolPairs.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var openBuys = openOrders
            .Where(o => o.IsOpen && o.Side == OrderSide.Buy && allowed.Contains(MarketDataPoller.NormaliseSymbol(o.Symbol)))
            .Sum(o => Math.Max(0m, o.Quantity - o.FilledQuantity) * (o.Price ?? 0m));

        // Sent but not listed yet (or outcome unknown): count it, so a slow exchange can't let us exceed the allocation.
        var listed = openOrders.Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var pendingBuys = pending
            .Where(p => p.Order.Side == OrderSide.Buy && !listed.Contains(p.Order.ClientOrderId))
            .Sum(p => p.Order.NotionalQuote);

        var equity = TotalOf(balances, quoteCurrency) + positions.Values.Sum();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var state = store.Update(s => s with
        {
            PeakEquity = Math.Max(s.PeakEquity, equity),
            Day = today,
            DayStartEquity = s.Day == today ? s.DayStartEquity : equity,
        });

        return new PortfolioSnapshot(
            Equity: equity,
            PeakEquity: state.PeakEquity,
            PnlToday: equity - state.DayStartEquity,
            TotalExposureQuote: positions.Values.Sum() + openBuys + pendingBuys,
            PositionQuoteBySymbol: positions,
            OrdersInLastMinute: ordersInLastMinute,
            KillSwitchEngaged: killSwitch.IsEngaged);
    }

    private static decimal TotalOf(IReadOnlyList<Balance> balances, string currency) =>
        balances.Where(b => string.Equals(b.Currency, currency, StringComparison.OrdinalIgnoreCase)).Sum(b => b.Total);
}
