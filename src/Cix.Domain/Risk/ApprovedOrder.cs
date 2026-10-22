using Cix.Domain.Exchange;

namespace Cix.Domain.Risk;

/// <summary>
/// An order the <see cref="RiskGate"/> accepted, possibly resized. Only the gate can create one (the constructor is
/// internal to <c>Cix.Domain</c>), and <see cref="IExchangeClient.PlaceLimitOrderAsync"/> accepts nothing else, so
/// the compiler enforces that every order passes the gate. A class rather than a record, so no <c>with</c> copy can
/// change its size or price after approval.
/// </summary>
public sealed class ApprovedOrder
{
    internal ApprovedOrder(OrderIntent order, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Symbol = order.Symbol;
        Side = order.Side;
        BaseSize = order.BaseSize;
        LimitPrice = order.LimitPrice;
        ClientOrderId = order.ClientOrderId;
        ApprovedAt = approvedAt;
    }

    public string Symbol { get; }

    public OrderSide Side { get; }

    public decimal BaseSize { get; }

    public decimal LimitPrice { get; }

    public string ClientOrderId { get; }

    /// <summary>When the gate approved it; the approval holds only for the market and portfolio it saw then.</summary>
    public DateTimeOffset ApprovedAt { get; }

    public decimal NotionalQuote => BaseSize * LimitPrice;

    /// <summary>The approved order as a plain intent, for logging and the journal. It can't be placed.</summary>
    public OrderIntent ToIntent() => new(Symbol, Side, BaseSize, LimitPrice, ClientOrderId);

    public override string ToString() => $"{Side} {BaseSize} {Symbol} @ {LimitPrice} ({ClientOrderId})";
}
