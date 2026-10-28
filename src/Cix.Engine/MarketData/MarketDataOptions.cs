namespace Cix.Engine.MarketData;

/// <summary>Polling schedule. Bound from the "MarketData" section; symbols come from the risk allowlist.</summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>Revolut X public endpoints allow 1 request per second; stay well below 60 a minute.</summary>
    public const int PublicRequestsPerMinute = 60;

    /// <summary>Order-book quotes per symbol. The code default of 10 seconds fits 7 symbols; appsettings.json sets 20 seconds, which fits 15 (ADR-0006).</summary>
    public TimeSpan QuoteInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often to refresh every symbol's bid and ask in one tickers request, when the exchange client offers one (live, over
    /// the signed endpoint and its 1,000 a minute budget; paper has none and keeps the order-book quotes alone). Null turns it
    /// off. The stop-loss protection sees a price move this soon instead of at the next order-book quote.
    /// </summary>
    public TimeSpan? TickerInterval { get; set; } = TimeSpan.FromSeconds(2);

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

    /// <summary>Revolut X signed endpoints allow 1,000 requests a minute (and 100 a second); the live quotes and tickers use them.</summary>
    public const int SignedRequestsPerMinute = 1_000;

    /// <summary>Planned steady-state public requests per minute for <paramref name="symbolCount"/> symbols (quotes and candles; paper's whole schedule).</summary>
    public decimal PlannedRequestsPerMinute(int symbolCount) =>
        symbolCount * (PerMinute(QuoteInterval) + (MinuteCandleInterval is { } minute ? PerMinute(minute) : 0m) + PerMinute(HourCandleInterval));

    /// <summary>Planned steady-state signed requests per minute when the quotes and tickers go over the signed endpoints (live).</summary>
    public decimal PlannedSignedRequestsPerMinute(int symbolCount) =>
        symbolCount * PerMinute(QuoteInterval) + (TickerInterval is { } tickers ? PerMinute(tickers) : 0m);

    /// <summary>
    /// Throws if the schedule would exceed the rate-limit budget: the public one for the whole schedule (paper, and live's
    /// candles), and with <paramref name="signedQuotes"/> also the signed one for the quotes and tickers (live). The public check
    /// stays for live too, so a schedule that fits live still fits paper, where the same settings file runs.
    /// </summary>
    public void Validate(int symbolCount, bool signedQuotes = false)
    {
        if (signedQuotes)
        {
            var signed = PlannedSignedRequestsPerMinute(symbolCount);
            var signedBudget = SignedRequestsPerMinute * MaxBudgetShare;
            if (signed > signedBudget)
                throw new InvalidOperationException(
                    $"Live quotes and tickers would need {signed:0.#} signed requests a minute for {symbolCount} symbols; the budget is {signedBudget:0.#}.");
        }

        if (QuoteInterval <= TimeSpan.Zero || MinuteCandleInterval <= TimeSpan.Zero || HourCandleInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("MarketData intervals must be positive.");
        if (TickerInterval is { } tickers && (tickers < TimeSpan.FromSeconds(1) || tickers > TimeSpan.FromMinutes(1)))
            throw new InvalidOperationException("MarketData:TickerInterval must be between 1 second and 1 minute (or null to turn tickers off).");
        if (MaxBudgetShare is <= 0 or > 1)
            throw new InvalidOperationException("MarketData:MaxBudgetShare must be above 0 and at most 1.");
        if (MaxCatchUpPages < 1)
            throw new InvalidOperationException("MarketData:MaxCatchUpPages must be at least 1.");

        var planned = PlannedRequestsPerMinute(symbolCount);
        var budget = PublicRequestsPerMinute * MaxBudgetShare;
        if (planned > budget)
            throw new InvalidOperationException(
                $"Market data would need {planned:0.#} requests a minute for {symbolCount} symbols; the budget is {budget:0.#}. Poll less often or trade fewer symbols.");
    }

    private static decimal PerMinute(TimeSpan interval) => 60m / (decimal)interval.TotalSeconds;
}
