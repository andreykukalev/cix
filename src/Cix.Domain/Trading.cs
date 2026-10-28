namespace Cix.Domain;

public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>How long an order may work on the book.</summary>
public enum TimeInForce
{
    /// <summary>Rests until filled or cancelled.</summary>
    GoodTillCancelled,

    /// <summary>Fills what it can at once; the exchange cancels the rest. It never rests on the book.</summary>
    ImmediateOrCancel,
}

/// <summary>Why an order is placed, as far as the risk gate cares.</summary>
public enum OrderPurpose
{
    /// <summary>An agent's, the strategy's or the owner's order: every limit applies, and a halt blocks it.</summary>
    Normal,

    /// <summary>
    /// The engine's own stop-loss sell (<c>ProtectionService</c>): an immediate-or-cancel sell of a held position. It only
    /// reduces risk, so a halt (the owner's pause or a safety halt) doesn't block it; every other check still applies.
    /// </summary>
    ProtectiveExit,
}

/// <summary>An order an agent (or strategy) wants to place, before risk checks.</summary>
/// <param name="Symbol">Exchange symbol, for example <c>BTC-EUR</c>.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="BaseSize">Quantity in the base currency.</param>
/// <param name="LimitPrice">Limit price in the quote currency.</param>
/// <param name="ClientOrderId">Idempotency key; the same intent must reuse it on retry.</param>
/// <param name="Purpose">Normal, or the engine's protective exit (see <see cref="OrderPurpose"/>).</param>
/// <param name="TimeInForce">Rests (the default) or immediate-or-cancel.</param>
public sealed record OrderIntent(
    string Symbol,
    OrderSide Side,
    decimal BaseSize,
    decimal LimitPrice,
    string ClientOrderId,
    OrderPurpose Purpose = OrderPurpose.Normal,
    TimeInForce TimeInForce = TimeInForce.GoodTillCancelled)
{
    public decimal NotionalQuote => BaseSize * LimitPrice;

    /// <summary>An engine stop-loss sell: immediate-or-cancel, so it never rests and can't be left working by a halt.</summary>
    public bool IsProtectiveExit => Purpose == OrderPurpose.ProtectiveExit && Side == OrderSide.Sell && TimeInForce == TimeInForce.ImmediateOrCancel;
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
