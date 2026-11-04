namespace Cix.Stocks;

/// <summary>
/// One news article about a stock, as the provider lists it: what the analyst reads beside the advice (ADR-0027). The text
/// is the provider's, cleaned of characters that could disguise it; it is data for the analyst, never instructions.
/// </summary>
/// <param name="Summary">The start of the article, at most a few hundred characters.</param>
/// <param name="Source">The site it was published on, for example <c>reuters.com</c>.</param>
/// <param name="Polarity">The provider's sentiment, from -1 (negative) to 1 (positive), when it gives one.</param>
public sealed record NewsItem(DateTimeOffset Published, string Title, string? Summary, string? Source, decimal? Polarity);

/// <summary>Recent news about a stock from a market data provider.</summary>
public interface INewsSource
{
    /// <summary>The newest articles about <paramref name="symbol"/> since <paramref name="from"/>, at most <paramref name="limit"/>.</summary>
    /// <exception cref="ArgumentException">The symbol isn't <c>TICKER.EXCHANGE</c>, or the limit is out of range.</exception>
    /// <exception cref="StockDataException">The provider refused the request or answered with something unreadable.</exception>
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, DateOnly from, int limit, CancellationToken cancellationToken);
}
