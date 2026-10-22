namespace Cix.Engine.MarketData;

/// <summary>Polling schedule. Bound from the "MarketData" section; symbols come from the risk allowlist.</summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>Revolut X public endpoints allow 1 request per second; stay well below 60 a minute.</summary>
    public const int PublicRequestsPerMinute = 60;

    public TimeSpan QuoteInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to fetch new 1-minute candles.</summary>
    public TimeSpan MinuteCandleInterval { get; set; } = TimeSpan.FromMinutes(1);

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

    /// <summary>Planned steady-state requests per minute for <paramref name="symbolCount"/> symbols.</summary>
    public decimal PlannedRequestsPerMinute(int symbolCount) =>
        symbolCount * (PerMinute(QuoteInterval) + PerMinute(MinuteCandleInterval) + PerMinute(HourCandleInterval));

    /// <summary>Throws if the schedule would exceed the public rate-limit budget.</summary>
    public void Validate(int symbolCount)
    {
        if (QuoteInterval <= TimeSpan.Zero || MinuteCandleInterval <= TimeSpan.Zero || HourCandleInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("MarketData intervals must be positive.");
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
