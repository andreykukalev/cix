using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>Latest quote per symbol. The risk gate prices orders from <see cref="Snapshot"/>.</summary>
public sealed class MarketState(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MarketQuote> _quotes = new(StringComparer.Ordinal);

    /// <summary>
    /// Stores the quote unless it is invalid (zero or negative prices, crossed book), from the future, or older than the
    /// one held. Rejected quotes leave the last valid one in place, which then ages until the risk gate refuses it.
    /// Returns whether the quote was accepted.
    /// </summary>
    public bool Update(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (!quote.IsValid || quote.AsOf > time.GetUtcNow())
            return false;

        var symbol = quote.Symbol.ToUpperInvariant();
        lock (_gate)
        {
            if (_quotes.TryGetValue(symbol, out var current) && current.AsOf > quote.AsOf)
                return false;

            _quotes[symbol] = quote with { Symbol = symbol };
            return true;
        }
    }

    public MarketQuote? Quote(string symbol)
    {
        lock (_gate)
        {
            return _quotes.TryGetValue(symbol.ToUpperInvariant(), out var quote) ? quote : null;
        }
    }

    /// <summary>
    /// Reference price for the risk gate, keeping the quote's own timestamp so the gate rejects stale data.
    /// Returns a snapshot with price 0 when there is no quote, which the gate also rejects.
    /// </summary>
    public MarketSnapshot Snapshot(string symbol)
    {
        var quote = Quote(symbol);
        return quote is null
            ? new MarketSnapshot(symbol.ToUpperInvariant(), 0m, DateTimeOffset.MinValue)
            : new MarketSnapshot(quote.Symbol, quote.Mid, quote.AsOf);
    }
}
