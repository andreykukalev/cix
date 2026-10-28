using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>The portfolio can't be valued safely (no balances, or a held asset has no fresh price).</summary>
public sealed class PortfolioUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An order the engine sent that may not show in open orders yet, or whose placement outcome is unknown.</summary>
/// <param name="VenueOrderId">Set once the exchange confirmed it, so the kill switch can cancel it before it is listed.</param>
public sealed record PendingPlacement(ApprovedOrder Order, DateTimeOffset SentAt, bool Uncertain, string? VenueOrderId = null);

/// <summary>
/// Builds the risk gate's <see cref="PortfolioSnapshot"/> from exchange balances, open orders and current prices, so it
/// recovers the true state after any restart. Tracks peak equity and the equity at 00:00 UTC in the engine state store.
/// </summary>
/// <remarks>
/// Assumes a dedicated account: equity is the balance in the owner's quote currency plus the base balances of the watched
/// pairs, all quoted in it. Exposure is positions plus open and just-sent buy orders.
/// </remarks>
public sealed class PortfolioService(
    IExchangeClient exchange,
    PairCatalog pairs,
    MarketState market,
    KillSwitch killSwitch,
    IEngineStateStore store,
    RiskLimits limits,
    TimeProvider time,
    TradableSymbols? tradable = null)
{
    /// <summary>The pairs tradable now. Removing a pair is refused while it is held, so equity never drops a holding.</summary>
    private IReadOnlySet<string> Tradable => tradable?.Current ?? TradableSymbols.None;

    /// <param name="skipUnpriced">
    /// For protective exits only: a held asset without a fresh price is left out instead of failing the valuation, so one
    /// coin's stale quote can't block the stop-loss of another. The equity is then partial, so the peak and the 00:00 UTC
    /// baseline aren't updated from it; a sell needs neither.
    /// </param>
    public async Task<PortfolioSnapshot> GetSnapshotAsync(
        int ordersInLastMinute,
        IReadOnlyCollection<PendingPlacement> pending,
        CancellationToken cancellationToken,
        bool skipUnpriced = false)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var symbolPairs = new List<PairInfo>();
        var unlisted = new List<string>();
        foreach (var symbol in Tradable.Order(StringComparer.Ordinal))
        {
            PairInfo? pair;
            try
            {
                pair = await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
            {
                throw new PortfolioUnavailableException("The exchange's trading rules could not be loaded.", ex);
            }

            if (pair is null)
                unlisted.Add(symbol); // fails the valuation below only if something is held or working in it
            else
                symbolPairs.Add(pair);
        }

        // The owner's quote currency, even with nothing watched yet: equity is then the cash alone.
        var quoteCurrency = limits.AllowedQuoteCurrency;
        if (symbolPairs.FirstOrDefault(p => !string.Equals(p.Quote, quoteCurrency, StringComparison.OrdinalIgnoreCase)) is { } other)
            throw new PortfolioUnavailableException($"Every watched pair must be quoted in {quoteCurrency}; {other.Symbol} is quoted in {other.Quote}.");

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

        // A watched pair the exchange no longer lists can't be priced: that matters only if the account holds it or has an
        // order in it. Otherwise one delisted coin nobody holds would block every order, the stop-loss sells included.
        foreach (var symbol in unlisted)
        {
            if (TotalOf(balances, symbol.Split('-')[0]) > 0m
                || openOrders.Any(o => o.IsOpen && MarketDataPoller.NormaliseSymbol(o.Symbol) == symbol)
                || pending.Any(p => p.Order.Symbol == symbol))
                throw new PortfolioUnavailableException($"The exchange doesn't list {symbol}.");
        }

        var now = time.GetUtcNow();
        var positions = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var sellable = new Dictionary<string, decimal>(StringComparer.Ordinal);
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
            {
                if (skipUnpriced)
                    continue;
                throw new PortfolioUnavailableException($"{pair.Base} is held but {pair.Symbol} has no fresh price.");
            }

            positions[pair.Symbol] = held * quote.Mid;

            // What can actually be sold: free (not reserved by an open sell) and at least the exchange minimum.
            var free = balances.Where(b => string.Equals(b.Currency, pair.Base, StringComparison.OrdinalIgnoreCase)).Sum(b => b.Available);
            var size = Steps.Floor(free, pair.BaseStep);
            if (size >= pair.MinOrderSize && size * quote.Bid >= pair.MinOrderSizeQuote)
                sellable[pair.Symbol] = size;
        }

        var allowed = symbolPairs.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var openBuyOrders = openOrders
            .Where(o => o.IsOpen && o.Side == OrderSide.Buy && allowed.Contains(MarketDataPoller.NormaliseSymbol(o.Symbol)))
            .ToList();
        if (openBuyOrders.Any(o => o.Price is null))
            throw new PortfolioUnavailableException("An open buy order has no limit price, so its exposure is unknown.");
        var buysBySymbol = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var o in openBuyOrders)
            Add(buysBySymbol, MarketDataPoller.NormaliseSymbol(o.Symbol), Math.Max(0m, o.Quantity - o.FilledQuantity) * o.Price!.Value);

        // Sent but not listed yet (or outcome unknown): count it, so a slow exchange can't let us exceed a cap.
        var listed = openOrders.Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        foreach (var p in pending.Where(p => p.Order.Side == OrderSide.Buy && !listed.Contains(p.Order.ClientOrderId)))
        {
            if (await IsFinishedAsync(p, cancellationToken).ConfigureAwait(false))
                continue; // filled or cancelled already: a fill is in the balances now
            Add(buysBySymbol, p.Order.Symbol, p.Order.NotionalQuote);
        }

        var equity = TotalOf(balances, quoteCurrency) + positions.Values.Sum();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var state = skipUnpriced ? store.Current : store.Update(s => s with
        {
            PeakEquity = Math.Max(s.PeakEquity, equity),
            Day = today,
            DayStartEquity = s.Day == today ? s.DayStartEquity : equity,
        });

        return new PortfolioSnapshot(
            Equity: equity,
            PeakEquity: state.PeakEquity,
            PnlToday: equity - state.DayStartEquity,
            TotalExposureQuote: positions.Values.Sum() + buysBySymbol.Values.Sum(),
            PositionQuoteBySymbol: positions,
            OrdersInLastMinute: ordersInLastMinute,
            KillSwitchEngaged: killSwitch.IsEngaged,
            OpenBuyQuoteBySymbol: buysBySymbol,
            SellableBaseBySymbol: sellable);
    }

    /// <summary>
    /// The value, at current prices, of the change from <paramref name="before"/> to <paramref name="after"/> holdings
    /// (currency → total). Fills move value between currencies at about the market price, so this stays near zero minus
    /// fees; a deposit, withdrawal or trade outside the engine shows up as a jump. Null when a changed allowed asset has
    /// no fresh price. Changes in assets outside the allowed symbols are ignored, as they are outside the allocation.
    /// </summary>
    public async Task<decimal?> ValueOfChangeAsync(
        IReadOnlyDictionary<string, decimal> before,
        IReadOnlyDictionary<string, decimal> after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var prices = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        var now = time.GetUtcNow();
        foreach (var symbol in Tradable)
        {
            var pair = await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (pair is null)
                continue;
            prices[pair.Quote] = 1m;
            var quote = market.Quote(pair.Symbol);
            prices[pair.Base] = quote is not null && now - quote.AsOf <= HardCeilings.MaxMarketDataAge ? quote.Mid : null;
        }

        var total = 0m;
        foreach (var currency in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var change = after.GetValueOrDefault(currency) - before.GetValueOrDefault(currency);
            if (change == 0 || !prices.TryGetValue(currency, out var price))
                continue;
            if (price is null)
                return null;
            total += change * price.Value;
        }

        return total;
    }

    /// <summary>True only when the exchange reports the order as finished; unknown (not visible yet) counts as working.</summary>
    private async Task<bool> IsFinishedAsync(PendingPlacement pending, CancellationToken cancellationToken)
    {
        if (pending.VenueOrderId is null)
            return false;
        try
        {
            var order = await exchange.GetOrderAsync(pending.VenueOrderId, cancellationToken).ConfigureAwait(false);
            return order is { IsOpen: false };
        }
        catch (ExchangeException)
        {
            return false;
        }
    }

    private static void Add(Dictionary<string, decimal> totals, string symbol, decimal amount) =>
        totals[symbol] = totals.GetValueOrDefault(symbol) + amount;

    private static decimal TotalOf(IReadOnlyList<Balance> balances, string currency) =>
        balances.Where(b => string.Equals(b.Currency, currency, StringComparison.OrdinalIgnoreCase)).Sum(b => b.Total);
}
