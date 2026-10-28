using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>
/// Decides what is due and fetches it: quotes for every symbol, then new closed 1-hour candles (and 1-minute candles when enabled).
/// Deterministic given the clock, so tests drive it with <see cref="PollOnceAsync"/>; <see cref="MarketDataService"/>
/// calls it every second. Failures are logged and retried at the next interval; they never stop the loop.
/// </summary>
public sealed partial class MarketDataPoller
{
    public const int MinuteInterval = 1;
    public const int HourInterval = 60;

    private const string TickersKey = "*";

    private readonly IMarketDataSource _candles;
    private readonly IQuoteSource _quotes;
    private readonly IBulkQuoteSource? _tickers;
    private readonly ICandleStore _store;
    private readonly MarketState _state;
    private readonly IReadOnlyList<IQuoteListener> _listeners;
    private readonly MarketDataOptions _options;
    private readonly Func<IReadOnlyList<string>> _symbols;
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
        ILogger<MarketDataPoller> logger,
        IBulkQuoteSource? tickers = null)
        : this(candles, quotes, store, state, listeners, options, Fixed(symbols), maxSymbols: null, time, logger, tickers)
    {
    }

    /// <summary>
    /// Polls whatever <paramref name="symbols"/> returns at each poll (the pairs tradable now, which the owner can change), up to
    /// <paramref name="maxSymbols"/>: the rate-limit check at startup covers that many.
    /// </summary>
    public MarketDataPoller(
        IMarketDataSource candles,
        IQuoteSource quotes,
        ICandleStore store,
        MarketState state,
        IEnumerable<IQuoteListener> listeners,
        MarketDataOptions options,
        Func<IReadOnlyList<string>> symbols,
        int? maxSymbols,
        TimeProvider time,
        ILogger<MarketDataPoller> logger,
        IBulkQuoteSource? tickers = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(options);
        _candles = candles;
        _quotes = quotes;
        _tickers = tickers;
        _store = store;
        _state = state;
        _listeners = listeners.ToList();
        _options = options;
        _symbols = symbols;
        _time = time;
        _logger = logger;

        var count = symbols().Count;
        if (count == 0)
            throw new InvalidOperationException("Market data needs at least one symbol.");
        options.Validate(Math.Max(count, maxSymbols ?? 0));
    }

    /// <summary>The symbols the next poll covers.</summary>
    public IReadOnlyList<string> Symbols => _symbols();

    private static Func<IReadOnlyList<string>> Fixed(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var list = NormaliseSymbols(symbols);
        return () => list;
    }

    /// <summary>
    /// Polls every due quote, then at most one due candle series (the one waiting longest), so catch-up paging or a
    /// throttled candle request never holds quotes back for more than one poll. Returns the exchange requests made.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        _requests = 0;
        var symbols = _symbols(); // one list per poll

        // Every symbol's bid and ask in one request, when the exchange offers tickers: the fastest view of a falling price.
        if (_tickers is not null && _options.TickerInterval is { } tickerInterval && DueSince(TickersKey, "tickers") is not null)
            await RunAsync(TickersKey, "tickers", tickerInterval, () => PollTickersAsync(symbols, cancellationToken)).ConfigureAwait(false);

        foreach (var symbol in symbols)
        {
            if (DueSince(symbol, "quote") is not null)
                await RunAsync(symbol, "quote", _options.QuoteInterval, () => PollQuoteAsync(symbol, cancellationToken)).ConfigureAwait(false);
        }

        var next = symbols
            .SelectMany(symbol => _options.MinuteCandleInterval is null
                ? new[] { (Symbol: symbol, Kind: "1h") }
                : new[] { (Symbol: symbol, Kind: "1m"), (Symbol: symbol, Kind: "1h") })
            .Select(t => (t.Symbol, t.Kind, Due: DueSince(t.Symbol, t.Kind)))
            .Where(t => t.Due is not null)
            .OrderBy(t => t.Due)
            .FirstOrDefault();

        if (next.Kind == "1m" && _options.MinuteCandleInterval is { } minuteInterval)
            await RunAsync(next.Symbol, "1m", minuteInterval, () => PollCandlesAsync(next.Symbol, MinuteInterval, _options.MinuteLookback, cancellationToken)).ConfigureAwait(false);
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

    /// <summary>
    /// One tickers request for all the symbols, handed on like polled quotes. A ticker carries no sizes, so the paper exchange
    /// (which fills against sizes) never runs on tickers: paper has no bulk source. The order-book quotes keep their own
    /// schedule, and the market state keeps whichever quote is newer.
    /// </summary>
    private async Task PollTickersAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
    {
        _requests++;
        var quotes = await _tickers!.GetQuotesAsync(symbols, cancellationToken).ConfigureAwait(false);
        var wanted = symbols.ToHashSet(StringComparer.Ordinal);
        var accepted = 0;
        foreach (var quote in quotes)
        {
            var symbol = NormaliseSymbol(quote.Symbol);
            if (!wanted.Contains(symbol))
                continue;
            if (!_state.Update(quote))
                continue;
            accepted++;
            Notify(quote with { Symbol = symbol }, symbol, except: null);
        }

        if (accepted < wanted.Count)
            LogTickersMissing(_logger, accepted, wanted.Count);
    }

    private void Notify(MarketQuote quote, string symbol, IQuoteListener? except)
    {
        foreach (var listener in _listeners)
        {
            if (ReferenceEquals(listener, except))
                continue;
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

    private async Task PollQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        _requests++;
        await FetchQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches <paramref name="symbol"/>'s quote now, outside the schedule, and hands it on like a polled one (the market
    /// state, then the listeners, such as the paper exchange). For the protective exits, which confirm a stop on a fresh book
    /// before selling. Returns the quote, or null if it was invalid or out of order; exchange errors propagate.
    /// <paramref name="except"/> is a listener that asked for the quote and reads the result itself, so it isn't told again:
    /// otherwise the protection's own confirming fetch would raise the stop it is already selling.
    /// </summary>
    public async Task<MarketQuote?> RefreshQuoteAsync(string symbol, CancellationToken cancellationToken, IQuoteListener? except = null) =>
        await FetchQuoteAsync(NormaliseSymbol(symbol), cancellationToken, except).ConfigureAwait(false);

    private async Task<MarketQuote?> FetchQuoteAsync(string symbol, CancellationToken cancellationToken, IQuoteListener? except = null)
    {
        var quote = await _quotes.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (!_state.Update(quote))
        {
            LogQuoteRejected(_logger, symbol);
            return null;
        }

        Notify(quote, symbol, except);
        return quote;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tickers covered {Covered} of {Wanted} symbols; the rest keep their order-book quotes")]
    private static partial void LogTickersMissing(ILogger logger, int covered, int wanted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an invalid, future or out-of-order quote for {Symbol}")]
    private static partial void LogQuoteRejected(ILogger logger, string symbol);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quote listener {Listener} failed for {Symbol}: {Detail}")]
    private static partial void LogListenerFailed(ILogger logger, string listener, string symbol, string detail);
}
