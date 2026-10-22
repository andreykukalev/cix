using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>Latest quote per symbol. The risk gate prices orders from <see cref="Snapshot"/>.</summary>
public sealed class MarketState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MarketQuote> _quotes = new(StringComparer.Ordinal);

    public void Update(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        lock (_gate)
        {
            if (!_quotes.TryGetValue(quote.Symbol, out var current) || current.AsOf <= quote.AsOf)
                _quotes[quote.Symbol] = quote;
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
