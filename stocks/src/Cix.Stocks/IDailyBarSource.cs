using Cix.Domain.Exchange;

namespace Cix.Stocks;

/// <summary>Daily bars from a market data provider, adjusted for splits and dividends.</summary>
public interface IDailyBarSource
{
    /// <summary>
    /// Bars from <paramref name="firstDay"/> to <paramref name="lastDay"/> inclusive, one per trading day, ascending; each bar's
    /// <see cref="Candle.Start"/> is its trading day at 00:00 UTC.
    /// </summary>
    /// <exception cref="ArgumentException">The symbol isn't <c>TICKER.EXCHANGE</c> (<see cref="StockSymbol.IsValid"/>).</exception>
    /// <exception cref="StockDataException">The provider refused the request or answered with something unreadable.</exception>
    Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string symbol, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>
/// The data provider failed. The message names the symbol and the HTTP status only: never the request URL, which carries
/// the provider's key.
/// </summary>
public sealed class StockDataException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
