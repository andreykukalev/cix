using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cix.Domain.Exchange;

namespace Cix.Stocks.MarketData;

/// <summary>
/// End-of-day bars from EODHD (<c>GET /api/eod/{TICKER.EXCHANGE}</c>, ADR-0023). Prices are adjusted for splits and
/// dividends: each bar's open, high and low are scaled by <c>adjusted_close / close</c>, so the whole history is in
/// today's terms and a split never looks like a crash.
/// <para>
/// EODHD takes its key only as the <c>api_token</c> query parameter, so the request URL is a secret: this client never
/// puts it in an exception or a log line, and the <see cref="HttpClient"/> it is given must not log request URLs
/// (don't register it with <c>IHttpClientFactory</c>'s default logging).
/// </para>
/// </summary>
public sealed class EodhdClient : IDailyBarSource
{
    public static readonly Uri DefaultBaseAddress = new("https://eodhd.com/api/");

    private const decimal MaxPrice = 10_000_000m;

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private readonly HttpClient _http;
    private readonly string _apiToken;

    /// <param name="http">With <see cref="HttpClient.BaseAddress"/> set, normally to <see cref="DefaultBaseAddress"/>.</param>
    /// <param name="apiToken">The EODHD key, from user secrets or Key Vault.</param>
    public EodhdClient(HttpClient http, string apiToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
            throw new ArgumentException("The HttpClient needs a BaseAddress.", nameof(http));
        if (http.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The key travels in the URL, so EODHD is only reached over https.", nameof(http));
        if (!http.BaseAddress.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("The BaseAddress must end with '/', or the request paths drop its last segment.", nameof(http));
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new ArgumentException("The EODHD key is missing.", nameof(apiToken));
        _http = http;
        _apiToken = apiToken;
    }

    public async Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string symbol, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        if (!StockSymbol.IsValid(symbol))
            throw new ArgumentException("The symbol must be TICKER.EXCHANGE, for example AAPL.US.", nameof(symbol));
        if (lastDay < firstDay)
            throw new ArgumentException("The range ends before it starts.", nameof(lastDay));

        var path = string.Create(CultureInfo.InvariantCulture,
            $"eod/{symbol}?from={firstDay:yyyy-MM-dd}&to={lastDay:yyyy-MM-dd}&period=d&fmt=json&api_token={Uri.EscapeDataString(_apiToken)}");

        EodBar[]? rows;
        try
        {
            using var response = await _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new StockDataException($"EODHD answered {(int)response.StatusCode} for {symbol}.", (int)response.StatusCode);

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            rows = await JsonSerializer.DeserializeAsync<EodBar[]>(body, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new StockDataException($"EODHD's answer for {symbol} isn't a list of daily bars.");
        }
        catch (HttpRequestException e)
        {
            // Not wrapped: an inner exception's text is out of this client's control.
            throw new StockDataException($"EODHD couldn't be reached for {symbol} ({e.HttpRequestError}).");
        }
        catch (IOException e)
        {
            // The connection dropped while the body was read.
            throw new StockDataException($"EODHD's answer for {symbol} was cut off ({e.GetType().Name}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StockDataException($"EODHD timed out for {symbol}.");
        }

        if (rows is null)
            throw new StockDataException($"EODHD's answer for {symbol} isn't a list of daily bars.");

        var bars = new SortedDictionary<DateOnly, Candle>();
        foreach (var row in rows)
        {
            if (ToCandle(row) is { } bar && TrendIndicators.TradingDay(bar) is var day && day >= firstDay && day <= lastDay)
                bars.TryAdd(day, bar);
        }

        return bars.Values.ToList();
    }

    /// <summary>The bar adjusted to today's terms, or null for a row with a missing, zero or inconsistent price.</summary>
    internal static Candle? ToCandle(EodBar row)
    {
        if (!DateOnly.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return null;
        if (row is not { Open: > 0, High: > 0, Low: > 0, Close: > 0 } || row.High < row.Low)
            return null;
        // No listed share costs ten million: such a row is garbage, and would overflow the averages built on it.
        if (Math.Max(Math.Max(row.Open.Value, row.High.Value), Math.Max(row.Close.Value, row.AdjustedClose ?? 0m)) > MaxPrice)
            return null;

        var close = row.AdjustedClose is > 0 ? row.AdjustedClose.Value : row.Close.Value;
        decimal Scale(decimal price) => Math.Round(price * close / row.Close.Value, 6, MidpointRounding.ToEven);

        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        try
        {
            return new Candle(start, Scale(row.Open.Value), Scale(row.High.Value), Scale(row.Low.Value), Math.Round(close, 6), Math.Max(0m, row.Volume ?? 0m));
        }
        catch (OverflowException)
        {
            return null; // An absurd adjustment: not a price to advise on.
        }
    }

    internal sealed record EodBar(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("open")] decimal? Open,
        [property: JsonPropertyName("high")] decimal? High,
        [property: JsonPropertyName("low")] decimal? Low,
        [property: JsonPropertyName("close")] decimal? Close,
        [property: JsonPropertyName("adjusted_close")] decimal? AdjustedClose,
        [property: JsonPropertyName("volume")] decimal? Volume);
}
