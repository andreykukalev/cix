namespace Cix.Domain.Exchange;

/// <summary>Current best bid and ask, with sizes, for one symbol.</summary>
public interface IQuoteSource
{
    Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken);
}

/// <summary>
/// Top-of-book quotes for many symbols in one request (an exchange's tickers endpoint). Cheaper than one order book per
/// symbol, so the engine can refresh every coin's price every few seconds; the quotes carry no sizes.
/// </summary>
public interface IBulkQuoteSource
{
    /// <summary>The latest quote for each of <paramref name="symbols"/> the exchange returned; symbols it doesn't list are left out.</summary>
    Task<IReadOnlyList<MarketQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken);
}

/// <summary>Receives every new quote the market-data poller fetches (for example the paper engine).</summary>
public interface IQuoteListener
{
    void OnQuote(MarketQuote quote);
}
