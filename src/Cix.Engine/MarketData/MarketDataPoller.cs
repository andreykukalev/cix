using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>
/// Decides what is due and fetches it: quotes for every symbol, then new closed 1-minute and 1-hour candles.
/// Deterministic given the clock, so tests drive it with <see cref="PollOnceAsync"/>; <see cref="MarketDataService"/>
/// calls it every second. Failures are logged and retried at the next interval; they never stop the loop.
/// </summary>
public sealed partial class MarketDataPoller
{
    public const int MinuteInterval = 1;
    public const int HourInterval = 60;

    private readonly IMarketDataSource _candles;
    private readonly IQuoteSource _quotes;
    private readonly ICandleStore _store;
    private readonly MarketState _state;
    private readonly IReadOnlyList<IQuoteListener> _listeners;
    private readonly MarketDataOptions _options;
    private readonly IReadOnlyList<string> _symbols;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketDataPoller> _logger;
    private readonly Dictionary<(string Symbol, string Kind), DateTimeOffset> _nextDue = [];
    private int _requests;

    public MarketDataPoller(
        IMarketDataSource candles,
        IQuoteSource quotes,
        ICandleStore store,
        MarketState state,
        IEnumerable<IQuoteListener> listeners,
        MarketDataOptions options,
        IEnumerable<string> symbols,
        TimeProvider time,
        ILogger<MarketDataPoller> logger)
    {
        _candles = candles;
        _quotes = quotes;
        _store = store;
        _state = state;
        _listeners = listeners.ToList();
        _options = options;
        _symbols = NormaliseSymbols(symbols);
        _time = time;
        _logger = logger;

        if (_symbols.Count == 0)
            throw new InvalidOperationException("Market data needs at least one symbol.");
        options.Validate(_symbols.Count);
    }

    public IReadOnlyList<string> Symbols => _symbols;

    /// <summary>
    /// Polls every due quote, then at most one due candle series (the one waiting longest), so catch-up paging or a
    /// throttled candle request never holds quotes back for more than one poll. Returns the exchange requests made.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        _requests = 0;
        foreach (var symbol in _symbols)
        {
            if (DueSince(symbol, "quote") is not null)
                await RunAsync(symbol, "quote", _options.QuoteInterval, () => PollQuoteAsync(symbol, cancellationToken)).ConfigureAwait(false);
        }

        var next = _symbols
            .SelectMany(symbol => new[] { (Symbol: symbol, Kind: "1m"), (Symbol: symbol, Kind: "1h") })
            .Select(t => (t.Symbol, t.Kind, Due: DueSince(t.Symbol, t.Kind)))
            .Where(t => t.Due is not null)
            .OrderBy(t => t.Due)
            .FirstOrDefault();

        if (next.Kind == "1m")
            await RunAsync(next.Symbol, "1m", _options.MinuteCandleInterval, () => PollCandlesAsync(next.Symbol, MinuteInterval, _options.MinuteLookback, cancellationToken)).ConfigureAwait(false);
        else if (next.Kind == "1h")
            await RunAsync(next.Symbol, "1h", _options.HourCandleInterval, () => PollCandlesAsync(next.Symbol, HourInterval, _options.HourLookback, cancellationToken)).ConfigureAwait(false);

        return _requests;
    }

    /// <summary>When the task became due (MinValue if it never ran), or null if it isn't due yet.</summary>
    private DateTimeOffset? DueSince(string symbol, string kind)
    {
        if (!_nextDue.TryGetValue((symbol, kind), out var due))
            return DateTimeOffset.MinValue;
        return _time.GetUtcNow() >= due ? due : null;
    }

    private async Task RunAsync(string symbol, string kind, TimeSpan interval, Func<Task> task)
    {
        // Schedule from the start time, so a slow request doesn't drift the cadence and a failure waits a full interval.
        _nextDue[(symbol, kind)] = _time.GetUtcNow() + interval;
        try
        {
            await task().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPollFailed(_logger, kind, symbol, ex.GetType().Name, ex.Message);
        }
    }

    private async Task PollQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        _requests++;
        var quote = await _quotes.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (!_state.Update(quote))
        {
            LogQuoteRejected(_logger, symbol);
            return;
        }

        foreach (var listener in _listeners)
        {
            try
            {
                listener.OnQuote(quote);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogListenerFailed(_logger, listener.GetType().Name, symbol, ex.Message);
            }
        }
    }

    /// <summary>Fetches closed candles after the latest stored one (or the lookback on first run), catching up in pages.</summary>
    private async Task PollCandlesAsync(string symbol, int intervalMinutes, TimeSpan lookback, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        for (var page = 0; page < _options.MaxCatchUpPages; page++)
        {
            var now = _time.GetUtcNow();
            var latest = _store.Latest(symbol, intervalMinutes);
            var since = latest is null ? AlignDown(now - lookback, interval) : latest.Start + interval;
            if (since + interval > now)
                break; // The next candle hasn't closed yet.

            _requests++;
            var fetched = await _candles.GetCandlesAsync(symbol, intervalMinutes, since, cancellationToken).ConfigureAwait(false);

            // Keep only closed candles at or after `since`; the open candle would change later.
            var closed = fetched.Where(c => c.Start >= since && c.Start + interval <= now).OrderBy(c => c.Start).ToList();
            if (latest is not null && closed.Count > 0 && closed[0].Start > since)
                LogCandleGap(_logger, intervalMinutes, symbol, since, closed[0].Start); // a quiet market, or the API skipped candles

            var added = _store.Upsert(symbol, intervalMinutes, closed);
            if (added == 0)
                break; // Nothing new (a quiet market can have no candles for a while).
        }
    }

    /// <summary>Upper-case dash form ("BTC-EUR"), de-duplicated and sorted.</summary>
    public static IReadOnlyList<string> NormaliseSymbols(IEnumerable<string> symbols) =>
        symbols.Select(NormaliseSymbol).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Upper-case dash form: "btc/eur" becomes "BTC-EUR".</summary>
    public static string NormaliseSymbol(string symbol) => symbol.Trim().Replace('/', '-').ToUpperInvariant();

    /// <summary>Rounds down to a candle boundary, so the first load starts with a whole candle.</summary>
    private static DateTimeOffset AlignDown(DateTimeOffset time, TimeSpan interval) =>
        new(time.UtcTicks - (time.UtcTicks % interval.Ticks), TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Market data {Kind} poll for {Symbol} failed ({Error}): {Detail}")]
    private static partial void LogPollFailed(ILogger logger, string kind, string symbol, string error, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "No {Interval}m candles for {Symbol} from {Since} until {First}")]
    private static partial void LogCandleGap(ILogger logger, int interval, string symbol, DateTimeOffset since, DateTimeOffset first);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an invalid, future or out-of-order quote for {Symbol}")]
    private static partial void LogQuoteRejected(ILogger logger, string symbol);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quote listener {Listener} failed for {Symbol}: {Detail}")]
    private static partial void LogListenerFailed(ILogger logger, string listener, string symbol, string detail);
}
