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
    private int _claimed;

    internal ApprovedOrder(OrderIntent order, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Symbol = order.Symbol;
        Side = order.Side;
        BaseSize = order.BaseSize;
        LimitPrice = order.LimitPrice;
        ClientOrderId = order.ClientOrderId;
        Purpose = order.Purpose;
        TimeInForce = order.TimeInForce;
        ApprovedAt = approvedAt;
    }

    public string Symbol { get; }

    public OrderSide Side { get; }

    public decimal BaseSize { get; }

    public decimal LimitPrice { get; }

    public string ClientOrderId { get; }

    public OrderPurpose Purpose { get; }

    public TimeInForce TimeInForce { get; }

    /// <summary>When the gate approved it; the approval holds only for the market and portfolio it saw then.</summary>
    public DateTimeOffset ApprovedAt { get; }

    public decimal NotionalQuote => BaseSize * LimitPrice;

    /// <summary>
    /// Marks the approval as used. Exchange clients call this before sending, so one approval sends at most one order:
    /// an old approval can't be replayed past the kill switch, rate limit or a newer portfolio.
    /// </summary>
    /// <exception cref="InvalidOperationException">The approval was already used.</exception>
    public void Claim()
    {
        if (Interlocked.Exchange(ref _claimed, 1) == 1)
            throw new InvalidOperationException("This approval was already used; ask the risk gate again.");
    }

    /// <summary>The approved order as a plain intent, for logging and the journal. It can't be placed.</summary>
    public OrderIntent ToIntent() => new(Symbol, Side, BaseSize, LimitPrice, ClientOrderId, Purpose, TimeInForce);

    public override string ToString() => $"{Side} {BaseSize} {Symbol} @ {LimitPrice} ({ClientOrderId})";
}
