namespace Cix.Domain.Exchange;

/// <summary>Current best bid and ask, with sizes, for one symbol.</summary>
public interface IQuoteSource
{
    Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken);
}

/// <summary>Receives every new quote the market-data poller fetches (for example the paper engine).</summary>
public interface IQuoteListener
{
    void OnQuote(MarketQuote quote);
}
