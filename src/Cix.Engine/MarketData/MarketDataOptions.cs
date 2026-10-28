namespace Cix.Engine.MarketData;

/// <summary>Polling schedule. Bound from the "MarketData" section; the symbols are the owner's watched pairs.</summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>Revolut X public endpoints allow 1 request per second; stay well below 60 a minute.</summary>
    public const int PublicRequestsPerMinute = 60;

    /// <summary>Revolut X signed endpoints allow 1,000 requests a minute (and 100 a second); the live quotes and tickers use them.</summary>
    public const int SignedRequestsPerMinute = 1_000;

    /// <summary>
    /// Order-book quotes per active coin (one with a position or an order), stretched when more active coins than the budget
    /// fits at this pace (<see cref="BookInterval"/>). Coins nobody holds or trades get tickers and candles only. The code
    /// default is 10 seconds; appsettings.json sets 20.
    /// </summary>
    public TimeSpan QuoteInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often to refresh every watched coin's bid and ask in one signed tickers request (live, within the 1,000 a minute
    /// budget). Null turns it off. The stop-loss protection sees a price move this soon instead of at the next order book.
    /// </summary>
    public TimeSpan? TickerInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The same over the public tickers endpoint (paper, 1 request a second shared with the candles and the order books): one
    /// request for every pair the exchange lists, so 50 coins cost what one does. Null turns it off.
    /// </summary>
    public TimeSpan? PublicTickerInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to fetch new 1-minute candles, or null (the default) to skip them; no feature uses them yet.</summary>
    public TimeSpan? MinuteCandleInterval { get; set; }

    /// <summary>How often to fetch new 1-hour candles.</summary>
    public TimeSpan HourCandleInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>History loaded on first start for 1-minute candles.</summary>
    public TimeSpan MinuteLookback { get; set; } = TimeSpan.FromHours(6);

    /// <summary>History loaded on first start for 1-hour candles (enough for EMA 50 and 24h comparisons).</summary>
    public TimeSpan HourLookback { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Most candle requests per series in one poll while catching up after downtime.</summary>
    public int MaxCatchUpPages { get; set; } = 5;

    /// <summary>Planned requests per minute may not exceed this share of the public limit.</summary>
    public decimal MaxBudgetShare { get; set; } = 0.8m;

    /// <summary>Quotes older than this make features stale (the risk gate has its own hard limit).</summary>
    public TimeSpan MaxQuoteAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The tickers interval that applies: the signed one in live, the public one in paper.</summary>
    public TimeSpan? TickerIntervalFor(bool signedQuotes) => signedQuotes ? TickerInterval : PublicTickerInterval;

    /// <summary>Candle requests a minute for <paramref name="symbolCount"/> coins (hourly, plus 1-minute when on): always public.</summary>
    public decimal CandleRequestsPerMinute(int symbolCount) =>
        symbolCount * ((MinuteCandleInterval is { } minute ? PerMinute(minute) : 0m) + PerMinute(HourCandleInterval));

    /// <summary>
    /// Planned public requests a minute for <paramref name="symbolCount"/> watched coins of which <paramref name="books"/>
    /// get an order book every <see cref="QuoteInterval"/>: the candles for all, and, unless the quotes are signed (live),
    /// the public tickers and the books too.
    /// </summary>
    public decimal PlannedPublicRequestsPerMinute(int symbolCount, int books, bool signedQuotes) =>
        CandleRequestsPerMinute(symbolCount)
        + (signedQuotes ? 0m : books * PerMinute(QuoteInterval) + TickersPerMinute(signedQuotes: false));

    /// <summary>Planned signed requests a minute (live): the tickers and <paramref name="books"/> order books every <see cref="QuoteInterval"/>.</summary>
    public decimal PlannedSignedRequestsPerMinute(int books) => books * PerMinute(QuoteInterval) + TickersPerMinute(signedQuotes: true);

    /// <summary>
    /// How often each of <paramref name="active"/> active coins gets its order book when <paramref name="symbolCount"/> coins
    /// are watched: <see cref="QuoteInterval"/>, or longer when that many books at that pace don't fit what the budget leaves
    /// after the tickers (when <paramref name="tickers"/> run) and the candles. Whole seconds, so the poller's cadence is plain.
    /// </summary>
    public TimeSpan BookInterval(int symbolCount, int active, bool signedQuotes, bool tickers = true)
    {
        if (active <= 0)
            return QuoteInterval;
        var left = signedQuotes
            ? SignedRequestsPerMinute * MaxBudgetShare - (tickers ? TickersPerMinute(signedQuotes: true) : 0m)
            : PublicRequestsPerMinute * MaxBudgetShare - CandleRequestsPerMinute(symbolCount) - (tickers ? TickersPerMinute(signedQuotes: false) : 0m);
        if (left <= 0m)
            return TimeSpan.FromMinutes(1); // can't happen after Validate; never divide by zero or poll faster than the budget allows
        var seconds = Math.Ceiling(active * 60m / left);
        return seconds > (decimal)QuoteInterval.TotalSeconds ? TimeSpan.FromSeconds((double)seconds) : QuoteInterval;
    }

    /// <summary>
    /// Throws if the schedule for <paramref name="symbolCount"/> watched coins can't fit: the candles for all of them, the
    /// tickers, and at least one coin's order book at <see cref="QuoteInterval"/> within the public budget (paper), or with
    /// <paramref name="signedQuotes"/> the candles within the public budget and the tickers plus every coin's book within the
    /// signed one (live). The public check stays for live too, so a schedule that fits live still fits paper, where the same
    /// settings file runs.
    /// </summary>
    public void Validate(int symbolCount, bool signedQuotes = false)
    {
        if (QuoteInterval <= TimeSpan.Zero || MinuteCandleInterval <= TimeSpan.Zero || HourCandleInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("MarketData intervals must be positive.");
        if (TickerInterval is { } tickers && (tickers < TimeSpan.FromSeconds(1) || tickers > TimeSpan.FromMinutes(1)))
            throw new InvalidOperationException("MarketData:TickerInterval must be between 1 second and 1 minute (or null to turn tickers off).");
        if (PublicTickerInterval is { } publicTickers && (publicTickers < TimeSpan.FromSeconds(1) || publicTickers > TimeSpan.FromMinutes(1)))
            throw new InvalidOperationException("MarketData:PublicTickerInterval must be between 1 second and 1 minute (or null to turn tickers off).");
        if (MaxBudgetShare is <= 0 or > 1)
            throw new InvalidOperationException("MarketData:MaxBudgetShare must be above 0 and at most 1.");
        if (MaxCatchUpPages < 1)
            throw new InvalidOperationException("MarketData:MaxCatchUpPages must be at least 1.");

        var publicBudget = PublicRequestsPerMinute * MaxBudgetShare;
        if (signedQuotes)
        {
            var signed = PlannedSignedRequestsPerMinute(symbolCount);
            var signedBudget = SignedRequestsPerMinute * MaxBudgetShare;
            if (signed > signedBudget)
                throw new InvalidOperationException(
                    $"Live tickers and order books would need {signed:0.#} signed requests a minute for {symbolCount} symbols; the budget is {signedBudget:0.#}.");
        }

        var planned = PlannedPublicRequestsPerMinute(symbolCount, books: 1, signedQuotes: false);
        if (planned > publicBudget)
            throw new InvalidOperationException(
                $"Market data would need {planned:0.#} public requests a minute for {symbolCount} symbols (candles, tickers and one order book); the budget is {publicBudget:0.#}. Poll less often or watch fewer symbols.");
    }

    private decimal TickersPerMinute(bool signedQuotes) => TickerIntervalFor(signedQuotes) is { } interval ? PerMinute(interval) : 0m;

    private static decimal PerMinute(TimeSpan interval) => 60m / (decimal)interval.TotalSeconds;
}
