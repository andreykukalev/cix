namespace Cix.Domain.Exchange;

/// <summary>Best bid and ask for one symbol at a moment, with the size available at each price.</summary>
public sealed record MarketQuote(
    string Symbol,
    decimal Bid,
    decimal BidSize,
    decimal Ask,
    decimal AskSize,
    DateTimeOffset AsOf)
{
    public decimal Mid => (Bid + Ask) / 2m;

    /// <summary>Positive prices, non-negative sizes and an uncrossed book. Anything else must never price an order.</summary>
    public bool IsValid => Bid > 0 && Ask > 0 && Bid <= Ask && BidSize >= 0 && AskSize >= 0;
}
