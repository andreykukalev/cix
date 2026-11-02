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
public sealed class EodhdClient : IDailyBarSource, IMarketSource, INewsSource
{
    public static readonly Uri DefaultBaseAddress = new("https://eodhd.com/api/");

    private const decimal MaxPrice = 10_000_000m;

    /// <summary>More rows than any exchange lists (the US download is about 50,000): a broken or hostile answer.</summary>
    private const int MaxRows = 200_000;

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

    /// <summary>
    /// The whole exchange's last session (<c>GET /api/eod-bulk-last-day/{EXCHANGE}?filter=extended</c>): 100 calls, whatever
    /// the number of rows, and only on paid keys (the free key answers with an error). Rows with a missing or nonsensical
    /// price are dropped; the provider's averages and market cap stay null when absent or zero.
    /// </summary>
    public async Task<IReadOnlyList<MarketRow>> GetMarketDayAsync(string exchange, CancellationToken cancellationToken)
    {
        if (!IsExchange(exchange))
            throw new ArgumentException("The exchange must be an EODHD exchange code, for example US.", nameof(exchange));
        var result = new List<MarketRow>();
        var count = 0;
        await foreach (var row in GetJsonRowsAsync<BulkRow>(
            $"eod-bulk-last-day/{exchange}?filter=extended&fmt=json&api_token={Uri.EscapeDataString(_apiToken)}", $"the {exchange} download", cancellationToken)
            .ConfigureAwait(false))
        {
            if (++count > MaxRows)
                throw new StockDataException($"EODHD's answer for the {exchange} download has over {MaxRows} rows.");
            if (row is null)
                continue;
            if (row.Code is not { Length: > 0 and <= 20 } code || !DateOnly.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;
            var symbol = $"{code}.{exchange}";
            var close = row.AdjustedClose is > 0 ? row.AdjustedClose : row.Close;
            if (!StockSymbol.IsValid(symbol) || close is not > 0 || close > MaxPrice)
                continue;
            result.Add(new(symbol, day, close.Value, Math.Max(0m, row.Volume ?? 0m), Positive(row.MarketCapitalization), Positive(row.Ema50),
                Positive(row.Ema200), Positive(row.High250), Positive(row.AverageVolume50)));
        }

        return result;
    }

    /// <summary>The exchange's listed instruments of one type (<c>GET /api/exchange-symbol-list/{EXCHANGE}?type=...</c>): 1 call.</summary>
    public async Task<IReadOnlyList<ListedStock>> GetListedStocksAsync(string exchange, CancellationToken cancellationToken)
    {
        if (!IsExchange(exchange))
            throw new ArgumentException("The exchange must be an EODHD exchange code, for example US.", nameof(exchange));
        var result = new List<ListedStock>();
        var count = 0;
        await foreach (var row in GetJsonRowsAsync<SymbolRow>(
            $"exchange-symbol-list/{exchange}?type=common_stock&fmt=json&api_token={Uri.EscapeDataString(_apiToken)}", $"the {exchange} symbol list", cancellationToken)
            .ConfigureAwait(false))
        {
            if (++count > MaxRows)
                throw new StockDataException($"EODHD's answer for the {exchange} symbol list has over {MaxRows} rows.");
            if (row?.Code is not { Length: > 0 and <= 20 } code || $"{code}.{exchange}" is var symbol && !StockSymbol.IsValid(symbol))
                continue;
            result.Add(new(symbol, Clean(row.Name) ?? code, Clean(row.Exchange) ?? "", Clean(row.Type) ?? ""));
        }

        return result;
    }

    /// <summary>The most articles one request asks for: the analyst reads a handful per stock.</summary>
    public const int MaxNews = 20;

    /// <summary>
    /// The newest articles about a stock (<c>GET /api/news?s={SYMBOL}&amp;from=...&amp;limit=...</c>): 5 calls each, on every plan.
    /// Rows without a date or a title are dropped; the title is cut at 200 characters and the summary at 300, both cleaned
    /// like every provider text (<see cref="Clean(string?, int)"/>), and the source is the link's host only.
    /// </summary>
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, DateOnly from, int limit, CancellationToken cancellationToken)
    {
        if (!StockSymbol.IsValid(symbol))
            throw new ArgumentException("The symbol must be TICKER.EXCHANGE, for example AAPL.US.", nameof(symbol));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxNews);
        var path = string.Create(CultureInfo.InvariantCulture,
            $"news?s={Uri.EscapeDataString(symbol)}&from={from:yyyy-MM-dd}&limit={limit}&offset=0&fmt=json&api_token={Uri.EscapeDataString(_apiToken)}");
        var result = new List<NewsItem>();
        var count = 0;
        await foreach (var row in GetJsonRowsAsync<NewsRow>(path, $"the news on {symbol}", cancellationToken).ConfigureAwait(false))
        {
            if (++count > MaxNews * 5)
                throw new StockDataException($"EODHD's answer for the news on {symbol} has far more rows than asked for.");
            if (result.Count == limit || row is null)
                continue;
            if (!DateTimeOffset.TryParse(row.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)
                || Clean(row.Title, 200) is not { } title)
                continue;
            var source = Uri.TryCreate(row.Link, UriKind.Absolute, out var link) && link.Scheme is "https" or "http"
                ? Clean(link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host) : null;
            var polarity = row.Sentiment?.Polarity is { } p && p is >= -1m and <= 1m ? Math.Round(p, 3, MidpointRounding.ToEven) : (decimal?)null;
            result.Add(new(published.ToUniversalTime(), title, Clean(row.Content, 300), source, polarity));
        }

        return result;
    }

    private static bool IsExchange(string exchange) => exchange is { Length: > 0 and <= 10 } && exchange.All(char.IsAsciiLetterUpper);

    private static decimal? Positive(decimal? value) => value is > 0 and < 1e15m ? value : null;

    /// <summary>
    /// Provider text without control, format (bidi overrides, zero-width) or line-separator characters, at most 80 UTF-16
    /// units and never cut inside a surrogate pair: it is shown on the page and, later, read by the analyst.
    /// </summary>
    internal static string? Clean(string? text, int max = 80)
    {
        // Line breaks and tabs become spaces (an article's text has them); runs of spaces become one.
        var kept = (text ?? "").Select(c => c is '\n' or '\r' or '\t' ? ' ' : c).Where(c => char.GetUnicodeCategory(c) is not (
            System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator));
        var s = string.Join(' ', new string(kept.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > max)
            s = s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// One GET whose answer is a JSON array. The body is buffered (up to the client's MaxResponseContentBufferSize), then read
    /// row by row, so the rows are never one huge array besides it; failures as <see cref="StockDataException"/>, never with
    /// the URL.
    /// </summary>
    private async IAsyncEnumerable<T?> GetJsonRowsAsync<T>(string path, string what, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new StockDataException($"EODHD couldn't be reached for {what} ({e.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StockDataException($"EODHD timed out for {what}.");
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new StockDataException($"EODHD answered {(int)response.StatusCode} for {what}.", (int)response.StatusCode);
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var rows = JsonSerializer.DeserializeAsyncEnumerable<T>(body, Json, cancellationToken).GetAsyncEnumerator(cancellationToken);
                await using (rows.ConfigureAwait(false))
                {
                    while (true)
                    {
                        T? row;
                        try
                        {
                            if (!await rows.MoveNextAsync().ConfigureAwait(false))
                                break;
                            row = rows.Current;
                        }
                        catch (JsonException)
                        {
                            throw new StockDataException($"EODHD's answer for {what} isn't the expected list.");
                        }
                        catch (IOException e)
                        {
                            throw new StockDataException($"EODHD's answer for {what} was cut off ({e.GetType().Name}).");
                        }

                        yield return row;
                    }
                }
            }
        }
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

    internal sealed record BulkRow(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("close")] decimal? Close,
        [property: JsonPropertyName("adjusted_close")] decimal? AdjustedClose,
        [property: JsonPropertyName("volume")] decimal? Volume,
        [property: JsonPropertyName("MarketCapitalization")] decimal? MarketCapitalization,
        [property: JsonPropertyName("ema_50d")] decimal? Ema50,
        [property: JsonPropertyName("ema_200d")] decimal? Ema200,
        [property: JsonPropertyName("hi_250d")] decimal? High250,
        [property: JsonPropertyName("avgvol_50d")] decimal? AverageVolume50);

    internal sealed record NewsRow(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("sentiment")] NewsSentiment? Sentiment);

    internal sealed record NewsSentiment([property: JsonPropertyName("polarity")] decimal? Polarity);

    internal sealed record SymbolRow(
        [property: JsonPropertyName("Code")] string? Code,
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("Exchange")] string? Exchange,
        [property: JsonPropertyName("Type")] string? Type);

    internal sealed record EodBar(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("open")] decimal? Open,
        [property: JsonPropertyName("high")] decimal? High,
        [property: JsonPropertyName("low")] decimal? Low,
        [property: JsonPropertyName("close")] decimal? Close,
        [property: JsonPropertyName("adjusted_close")] decimal? AdjustedClose,
        [property: JsonPropertyName("volume")] decimal? Volume);
}
