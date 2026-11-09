using System.Globalization;
using System.Net;
using System.Text.Json;
using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>Binance's public candles: read for signals only, never for prices or orders (ADR-0031).</summary>
public interface IBinanceKlines
{
    /// <summary>
    /// The latest <paramref name="limit"/> candles of <paramref name="symbol"/> (such as <c>BTCUSDT</c>) at
    /// <paramref name="intervalMinutes"/> (60 or 1440), oldest first, the one still forming included. Null when Binance doesn't
    /// list the symbol.
    /// </summary>
    Task<IReadOnlyList<Candle>?> GetAsync(string symbol, int intervalMinutes, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Binance's public market-data host, which needs no key and answers where api.binance.com is geo-blocked. Only GET requests
/// for candles: nothing about the account, nothing that trades.
/// </summary>
public sealed class BinanceKlinesClient(HttpClient http) : IBinanceKlines
{
    public static readonly Uri BaseAddress = new("https://data-api.binance.vision/");

    private const decimal MinPrice = 0.000000000001m;
    private const decimal MaxPrice = 1_000_000_000_000m;
    private const decimal MaxVolume = 100_000_000_000_000_000_000m;

    public async Task<IReadOnlyList<Candle>?> GetAsync(string symbol, int intervalMinutes, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!BinanceFeed.IsBinanceSymbol(symbol))
            throw new ArgumentException("Not a Binance symbol.", nameof(symbol));
        var interval = intervalMinutes switch
        {
            MarketDataPoller.HourInterval => "1h",
            MarketDataPoller.DayInterval => "1d",
            _ => throw new ArgumentOutOfRangeException(nameof(intervalMinutes)),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress,
            string.Create(CultureInfo.InvariantCulture, $"api/v3/klines?symbol={symbol}&interval={interval}&limit={Math.Clamp(limit, 1, 1000)}")));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            return null; // an unknown symbol (Binance code -1121); anything else is an error below
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    /// <summary>Rows of <c>[openTime, open, high, low, close, volume, ...]</c>, the prices as text.</summary>
    internal static IReadOnlyList<Candle> Parse(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array)
            throw new JsonException("Binance klines must be an array.");
        static decimal Number(JsonElement e) => e.ValueKind == JsonValueKind.String
            ? decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
            : e.GetDecimal();
        var candles = new List<Candle>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
                throw new JsonException("A Binance kline must have at least six fields.");
            var candle = new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()), Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5]));
            // Figures no market has: the whole answer is refused, so nothing downstream judges on them.
            if (candle.Low is < MinPrice or > MaxPrice || candle.High > MaxPrice || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close) || candle.Volume is < 0 or > MaxVolume)
                throw new JsonException("A Binance kline has prices or a volume out of range.");
            candles.Add(candle);
        }

        return candles.OrderBy(c => c.Start).ToList();
    }
}

/// <summary>
/// Keeps Binance's closed hourly candles for every watched coin (its <c>BASE</c>USDT pair) and BTC's daily candles, for the
/// plays' signals (ADR-0031). Revolut X's own volume is too thin to read a burst from; its prices still price every order.
/// A coin Binance doesn't list, or whose candles can't be read, simply has no burst. Polled by <see cref="BinanceFeedService"/>.
/// </summary>
/// <param name="active">Whether the candles are needed now (the running strategy has plays); null means always.</param>
public sealed partial class BinanceFeed(
    IBinanceKlines klines, Func<IReadOnlyList<string>> symbols, TimeProvider time, ILogger<BinanceFeed> logger, Func<bool>? active = null)
{
    /// <summary>Hourly candles kept per coin: a 168-hour volume average with room.</summary>
    public const int HourlyCandles = 300;

    /// <summary>BTC daily candles kept: a 150-day EMA with room.</summary>
    public const int DailyCandles = 200;

    /// <summary>Requests per poll, so a list of 50 coins is read over a minute rather than at once.</summary>
    public const int MaxRequestsPerPoll = 5;

    public const string Btc = "BTCUSDT";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnknownFor = TimeSpan.FromHours(24);

    private readonly InMemoryCandleStore _store = new(HourlyCandles * 2);
    private readonly Dictionary<(string Symbol, int Interval), DateTimeOffset> _notBefore = [];
    private readonly Lock _gate = new();

    /// <summary>The Binance pair a Revolut X symbol is judged on: <c>SOL-EUR</c> to <c>SOLUSDT</c>.</summary>
    public static string BinanceSymbolOf(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var normalised = MarketDataPoller.NormaliseSymbol(symbol);
        var dash = normalised.IndexOf('-', StringComparison.Ordinal);
        return (dash > 0 ? normalised[..dash] : normalised) + "USDT";
    }

    internal static bool IsBinanceSymbol(string symbol) => symbol.Length is > 4 and <= 20 && symbol.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    /// <summary>Closed hourly candles for a watched coin, oldest first; empty when there are none.</summary>
    public IReadOnlyList<Candle> Hourly(string symbol) => Closed(_store.Recent(BinanceSymbolOf(symbol), MarketDataPoller.HourInterval, HourlyCandles), Hour);

    /// <summary>BTC's closed daily candles (UTC days), oldest first.</summary>
    public IReadOnlyList<Candle> BtcDaily() => Closed(_store.Recent(Btc, MarketDataPoller.DayInterval, DailyCandles), Day);

    private List<Candle> Closed(IReadOnlyList<Candle> candles, TimeSpan length)
    {
        var now = time.GetUtcNow();
        return candles.Where(c => c.Start + length <= now).ToList();
    }

    /// <summary>Reads what is due: a series whose next candle has closed, at most <see cref="MaxRequestsPerPoll"/> requests.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (active?.Invoke() == false)
            return 0;
        var now = time.GetUtcNow();
        var due = symbols().Select(BinanceSymbolOf).Where(IsBinanceSymbol).Distinct(StringComparer.Ordinal)
            .Select(s => (Symbol: s, Interval: MarketDataPoller.HourInterval, Length: Hour, Count: HourlyCandles))
            .Prepend((Symbol: Btc, Interval: MarketDataPoller.DayInterval, Length: Day, Count: DailyCandles))
            .Where(s => IsDue(s.Symbol, s.Interval, s.Length, now))
            .Take(MaxRequestsPerPoll)
            .ToList();

        var read = 0;
        foreach (var (symbol, interval, length, count) in due)
        {
            var latest = _store.Latest(symbol, interval);
            var limit = latest is null ? count : (int)Math.Clamp(Math.Ceiling((now - latest.Start) / length) + 1, 2, count);
            try
            {
                var candles = await klines.GetAsync(symbol, interval, limit, cancellationToken).ConfigureAwait(false);
                if (candles is null)
                {
                    Defer(symbol, interval, now + UnknownFor);
                    LogUnknown(logger, symbol);
                    continue;
                }

                // Only closed candles: the one still forming changes until it closes, and the store keeps the first copy.
                _store.Upsert(symbol, interval, candles.Where(c => c.Start + length <= now));
                Defer(symbol, interval, DateTimeOffset.MinValue);
                read++;
            }
#pragma warning disable CA1031 // any failure of one series defers it, and the others are still read
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                Defer(symbol, interval, now + RetryAfter);
                LogFailed(logger, symbol, ex.GetType().Name, ex.Message);
            }
        }

        return read;
    }

    private bool IsDue(string symbol, int interval, TimeSpan length, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_notBefore.TryGetValue((symbol, interval), out var until) && now < until)
                return false;
        }

        // Due once a candle after the newest stored one has closed (a little after, so Binance has it).
        var latest = _store.Latest(symbol, interval);
        return latest is null || now >= latest.Start + length + length + TimeSpan.FromSeconds(20);
    }

    private void Defer(string symbol, int interval, DateTimeOffset until)
    {
        lock (_gate)
            _notBefore[(symbol, interval)] = until;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance doesn't list {Symbol}: no burst signals for it; asked again in a day")]
    private static partial void LogUnknown(ILogger logger, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance candles for {Symbol} not read ({Error}): {Detail}; tried again in two minutes")]
    private static partial void LogFailed(ILogger logger, string symbol, string error, string detail);
}

/// <summary>Polls <see cref="BinanceFeed"/> every 15 seconds; it decides what is due. A failed poll never stops the engine.</summary>
public sealed partial class BinanceFeedService(BinanceFeed feed, TimeProvider time, ILogger<BinanceFeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                await feed.PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // signals only: a Binance failure must not stop the engine that trades on Revolut X
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, ex.GetType().Name, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance candles poll failed ({Error}): {Detail}")]
    private static partial void LogPollFailed(ILogger logger, string error, string detail);
}
