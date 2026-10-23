namespace Cix.Domain;

public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>An order an agent (or strategy) wants to place, before risk checks.</summary>
/// <param name="Symbol">Exchange symbol, for example <c>BTC-EUR</c>.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="BaseSize">Quantity in the base currency.</param>
/// <param name="LimitPrice">Limit price in the quote currency.</param>
/// <param name="ClientOrderId">Idempotency key; the same intent must reuse it on retry.</param>
public sealed record OrderIntent(
    string Symbol,
    OrderSide Side,
    decimal BaseSize,
    decimal LimitPrice,
    string ClientOrderId)
{
    public decimal NotionalQuote => BaseSize * LimitPrice;
}

/// <summary>Portfolio state the risk gate needs, in the allocation's quote currency.</summary>
/// <param name="Equity">Quote balance plus positions at the reference price.</param>
/// <param name="PnlToday">Change in equity since 00:00 UTC, unrealised losses included, so losing positions count before they are sold.</param>
/// <param name="TotalExposureQuote">Positions plus open buy orders, in quote.</param>
/// <param name="OpenBuyQuoteBySymbol">Open and just-sent buy orders per symbol, in quote; they count toward the position cap.</param>
/// <param name="SellableBaseBySymbol">Base currency free to sell per symbol (not reserved by open sells), omitting amounts below the exchange minimum.</param>
public sealed record PortfolioSnapshot(
    decimal Equity,
    decimal PeakEquity,
    decimal PnlToday,
    decimal TotalExposureQuote,
    IReadOnlyDictionary<string, decimal> PositionQuoteBySymbol,
    int OrdersInLastMinute,
    bool KillSwitchEngaged,
    IReadOnlyDictionary<string, decimal>? OpenBuyQuoteBySymbol = null,
    IReadOnlyDictionary<string, decimal>? SellableBaseBySymbol = null);

/// <summary>Market reference for one symbol, typically the mid price.</summary>
public sealed record MarketSnapshot(string Symbol, decimal ReferencePrice, DateTimeOffset AsOf);
