using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>
/// The coins whose order books are worth polling: those with a position or an order, where a fill, a stop confirmation or
/// a paper fill needs sizes. Every other watched coin gets its price from the tickers.
/// </summary>
public interface IActiveSymbolSource
{
    IReadOnlySet<string> ActiveSymbols { get; }
}

/// <summary>
/// Decides what is due and fetches it: every watched coin's bid and ask in one tickers request, order books for the active
/// coins (<see cref="IActiveSymbolSource"/>) and for coins the tickers leave out, then new closed 1-hour candles (and 1-minute
/// candles when enabled). Deterministic given the clock, so tests drive it with <see cref="PollOnceAsync"/>;
/// <see cref="MarketDataService"/> runs the tickers (<see cref="PollTickersOnceAsync"/>) and the rest
/// (<see cref="PollBooksAndCandlesOnceAsync"/>) in two loops, every second each, so a slow order book or candle request never
/// holds the prices the stops watch (pre-live assessment F-05). Failures are logged and retried at the next interval; they
/// never stop the loop. An empty watched list polls nothing.
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
    private readonly Func<IReadOnlySet<string>>? _active;
    private readonly bool _signedQuotes;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketDataPoller> _logger;
    private readonly Lock _dueGate = new(); // the two loops share the schedule
    private readonly Dictionary<(string Symbol, string Kind), DateTimeOffset> _nextDue = [];
    private volatile IReadOnlyList<string> _tickersMissing = [];

    /// <summary>The exchange requests one poll made, counted as each is sent (a failed one counts too).</summary>
    private sealed class Tally
    {
        public int Requests;
    }

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
        IBulkQuoteSource? tickers = null,
        bool signedQuotes = false,
        Func<IReadOnlySet<string>>? active = null)
        : this(candles, quotes, store, state, listeners, options, Fixed(symbols), maxSymbols: null, time, logger, tickers, signedQuotes, active)
    {
    }

    /// <summary>
    /// Polls whatever <paramref name="symbols"/> returns at each poll (the pairs watched now, which the owner can change), up to
    /// <paramref name="maxSymbols"/>: the rate-limit check at startup covers that many. <paramref name="signedQuotes"/> says the
    /// quotes and tickers go over the signed endpoints (live) and their budget. <paramref name="active"/> names the coins whose
    /// order books are polled on the schedule; null polls every symbol's book, for engines without tickers and for tests.
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
        IBulkQuoteSource? tickers = null,
        bool signedQuotes = false,
        Func<IReadOnlySet<string>>? active = null)
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
        _active = active;
        _signedQuotes = signedQuotes;
        _time = time;
        _logger = logger;
        options.Validate(Math.Max(symbols().Count, maxSymbols ?? 0), signedQuotes);
    }

    /// <summary>The symbols the next poll covers.</summary>
    public IReadOnlyList<string> Symbols => _symbols();

    private static Func<IReadOnlyList<string>> Fixed(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var list = NormaliseSymbols(symbols);
        return () => list;
    }

    /// <summary>Whether this poller has tickers to poll, on their own loop (<see cref="PollTickersOnceAsync"/>).</summary>
    public bool HasTickers => _tickers is not null && _options.TickerIntervalFor(_signedQuotes) is not null;

    /// <summary>
    /// The tickers when due, then every due order book and at most one due candle series, one after the other: one round of
    /// both loops, for tests and for engines without tickers. Returns the exchange requests made.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken) =>
        await PollTickersOnceAsync(cancellationToken).ConfigureAwait(false)
        + await PollBooksAndCandlesOnceAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Every symbol's bid and ask in one request, when the exchange offers tickers and they are due: the fastest view of a
    /// falling price. Returns the exchange requests made.
    /// </summary>
    public async Task<int> PollTickersOnceAsync(CancellationToken cancellationToken)
    {
        var tally = new Tally();
        var symbols = _symbols(); // one list per poll
        if (symbols.Count == 0 || !HasTickers || _options.TickerIntervalFor(_signedQuotes) is not { } tickerInterval)
            return 0; // nothing watched, or no tickers: nothing to ask

        if (DueSince(TickersKey, "tickers") is not null)
            await RunAsync(TickersKey, "tickers", tickerInterval, () => PollTickersAsync(symbols, tally, cancellationToken)).ConfigureAwait(false);
        return tally.Requests;
    }

    /// <summary>
    /// Every due order book (the active coins, and those the tickers leave out), then at most one due candle series (the one
    /// waiting longest), so catch-up paging or a throttled candle request never holds the books back for more than one poll.
    /// Returns the exchange requests made.
    /// </summary>
    public async Task<int> PollBooksAndCandlesOnceAsync(CancellationToken cancellationToken)
    {
        var tally = new Tally();
        var symbols = _symbols(); // one list per poll
        if (symbols.Count == 0)
            return 0;

        // Order books carry sizes: for the coins with a position or an order (paper fills, stop confirmations), and for the
        // coins the tickers don't cover, which have no other price. Spread out when more are active than the budget fits.
        var tickers = HasTickers;
        var books = BookSymbols(symbols, tickers);
        var bookInterval = _options.BookInterval(symbols.Count, books.Count, _signedQuotes, tickers);
        foreach (var symbol in books)
        {
            if (DueSince(symbol, "quote") is not null)
                await RunAsync(symbol, "quote", bookInterval, () => PollQuoteAsync(symbol, tally, cancellationToken)).ConfigureAwait(false);
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
            await RunAsync(next.Symbol, "1m", minuteInterval, () => PollCandlesAsync(next.Symbol, MinuteInterval, _options.MinuteLookback, tally, cancellationToken)).ConfigureAwait(false);
        else if (next.Kind == "1h")
            await RunAsync(next.Symbol, "1h", _options.HourCandleInterval, () => PollCandlesAsync(next.Symbol, HourInterval, _options.HourLookback, tally, cancellationToken)).ConfigureAwait(false);

        return tally.Requests;
    }

    /// <summary>Which of <paramref name="symbols"/> get an order book on the schedule.</summary>
    private IReadOnlyList<string> BookSymbols(IReadOnlyList<string> symbols, bool tickers)
    {
        if (_active is null || !tickers)
            return symbols; // without tickers the books are the only prices
        var active = _active();
        var missing = _tickersMissing;
        return symbols.Where(s => active.Contains(s) || missing.Contains(s)).ToList();
    }

    /// <summary>The coins whose order books the next poll covers, for the status and tests.</summary>
    public IReadOnlyList<string> BookSymbols() => BookSymbols(_symbols(), HasTickers);

    /// <summary>When the task became due (MinValue if it never ran), or null if it isn't due yet.</summary>
    private DateTimeOffset? DueSince(string symbol, string kind)
    {
        lock (_dueGate)
        {
            if (!_nextDue.TryGetValue((symbol, kind), out var due))
                return DateTimeOffset.MinValue;
            return _time.GetUtcNow() >= due ? due : null;
        }
    }

    private async Task RunAsync(string symbol, string kind, TimeSpan interval, Func<Task> task)
    {
        // Schedule from the start time, so a slow request doesn't drift the cadence and a failure waits a full interval.
        lock (_dueGate)
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
    private async Task PollTickersAsync(IReadOnlyList<string> symbols, Tally tally, CancellationToken cancellationToken)
    {
        tally.Requests++;
        var quotes = await _tickers!.GetQuotesAsync(symbols, cancellationToken).ConfigureAwait(false);
        var wanted = symbols.ToHashSet(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quote in quotes)
        {
            var symbol = NormaliseSymbol(quote.Symbol);
            if (!wanted.Contains(symbol))
                continue;
            covered.Add(symbol);
            // Not newer than the book polled a moment ago: expected now and then, and nothing to log.
            if (_state.Update(quote))
                Notify(quote with { Symbol = symbol }, symbol, except: null);
        }

        // Say which symbols the exchange's tickers leave out, once per change, not every 2 seconds.
        var missing = wanted.Where(s => !covered.Contains(s)).Order(StringComparer.Ordinal).ToList();
        if (!missing.SequenceEqual(_tickersMissing, StringComparer.Ordinal))
        {
            _tickersMissing = missing;
            if (missing.Count > 0)
                LogTickersMissing(_logger, string.Join(", ", missing), wanted.Count);
        }
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

    private async Task PollQuoteAsync(string symbol, Tally tally, CancellationToken cancellationToken)
    {
        tally.Requests++;
        await FetchQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches <paramref name="symbol"/>'s quote now, outside the schedule, and hands it on like a polled one (the market
    /// state, then the listeners, such as the paper exchange). For the protective exits, which confirm a stop on a fresh book
    /// before selling. Returns the quote, or null if it was invalid or from the future; exchange errors propagate. A book the
    /// state keeps out only because a ticker carries a later exchange timestamp is still returned: the caller asked for the
    /// book as it is now, and whether the two endpoints stamp alike is the exchange's business, never a reason to leave a stop
    /// unsold. <paramref name="except"/> is a listener that asked for the quote and reads the result itself, so it isn't told
    /// again: otherwise the protection's own confirming fetch would raise the stop it is already selling.
    /// </summary>
    public async Task<MarketQuote?> RefreshQuoteAsync(string symbol, CancellationToken cancellationToken, IQuoteListener? except = null) =>
        await FetchQuoteAsync(NormaliseSymbol(symbol), cancellationToken, except).ConfigureAwait(false);

    private async Task<MarketQuote?> FetchQuoteAsync(string symbol, CancellationToken cancellationToken, IQuoteListener? except = null)
    {
        var quote = await _quotes.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (!quote.IsValid || quote.AsOf > _time.GetUtcNow())
        {
            LogQuoteRejected(_logger, symbol);
            return null;
        }

        // The state keeps the newer of this book and the latest ticker for the risk gate. Listeners hear what the state kept,
        // and also a book it kept out only because a size-less ticker carries a later stamp: the paper exchange fills against
        // books alone, so without this an order priced a moment after a ticker poll would fill on an older book.
        if (_state.Update(quote) || IsBookBehindATicker(symbol, quote))
            Notify(quote, symbol, except);
        return quote;
    }

    private bool IsBookBehindATicker(string symbol, MarketQuote quote) =>
        (quote.BidSize > 0 || quote.AskSize > 0)
        && _state.Quote(symbol) is { BidSize: <= 0, AskSize: <= 0 } ticker
        && ticker.AsOf > quote.AsOf;

    /// <summary>Fetches closed candles after the latest stored one (or the lookback on first run), catching up in pages.</summary>
    private async Task PollCandlesAsync(string symbol, int intervalMinutes, TimeSpan lookback, Tally tally, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        for (var page = 0; page < _options.MaxCatchUpPages; page++)
        {
            var now = _time.GetUtcNow();
            var latest = _store.Latest(symbol, intervalMinutes);
            var since = latest is null ? AlignDown(now - lookback, interval) : latest.Start + interval;
            if (since + interval > now)
                break; // The next candle hasn't closed yet.

            tally.Requests++;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "The tickers leave out {Missing} (of {Wanted} symbols); those keep their order-book quotes")]
    private static partial void LogTickersMissing(ILogger logger, string missing, int wanted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an invalid or future-dated quote for {Symbol}")]
    private static partial void LogQuoteRejected(ILogger logger, string symbol);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quote listener {Listener} failed for {Symbol}: {Detail}")]
    private static partial void LogListenerFailed(ILogger logger, string listener, string symbol, string detail);
}
