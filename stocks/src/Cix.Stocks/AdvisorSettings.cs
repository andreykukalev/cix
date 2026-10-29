namespace Cix.Stocks;

/// <summary>
/// The trend rules and the owner's risk budget. Defaults are a starting point for the backtest, not calibrated values.
/// Fractions are of the allocation (<see cref="AdvisorInput.Capital"/>); ATR multiples use <see cref="AtrDays"/>.
/// </summary>
public sealed record AdvisorSettings
{
    /// <summary>The index the market regime is read from, for example <c>CSPX.LSE</c> (S&amp;P 500, UCITS).</summary>
    public string BenchmarkSymbol { get; init; } = "CSPX.LSE";

    /// <summary>Risk-on while the benchmark closes above its moving average of this many days.</summary>
    public int RegimeSmaDays { get; init; } = 200;

    public int FastSmaDays { get; init; } = 50;

    /// <summary>The trend line: entries need the close above it, and a close below it ends the trend.</summary>
    public int SlowSmaDays { get; init; } = 200;

    /// <summary>An entry needs a close above the highest close of this many previous days.</summary>
    public int BreakoutDays { get; init; } = 50;

    public int AtrDays { get; init; } = 20;

    /// <summary>Ranking of entry candidates: the change over this many days, strongest first.</summary>
    public int MomentumDays { get; init; } = 126;

    public decimal InitialStopAtr { get; init; } = 3m;

    /// <summary>The trailing stop sits this many ATRs under the highest close since entry.</summary>
    public decimal TrailingStopAtr { get; init; } = 3m;

    /// <summary>The tighter trail used while the market is risk-off.</summary>
    public decimal RiskOffTrailingStopAtr { get; init; } = 2m;

    /// <summary>
    /// Assumed extra loss when the price opens below the stop (overnight gap), in ATRs. Sizing counts it as part of the risk,
    /// because a stop at the broker sells at the next price, not at the stop.
    /// </summary>
    public decimal GapAtr { get; init; } = 1m;

    /// <summary>The most a single position may lose at its stop plus the gap, as a fraction of the allocation.</summary>
    public decimal RiskPerTrade { get; init; } = 0.01m;

    public decimal MaxPositionFraction { get; init; } = 0.20m;

    public int MaxPositions { get; init; } = 8;

    /// <summary>A stop is only worth moving when it rises by at least this fraction, so the owner isn't asked daily for cents.</summary>
    public decimal MinStopRaiseFraction { get; init; } = 0.005m;

    /// <summary>Bars older than this many calendar days are stale (covers a weekend plus a holiday): no advice uses them.</summary>
    public int MaxBarAgeDays { get; init; } = 4;

    /// <summary>The fewest daily bars an instrument needs before anything is computed for it.</summary>
    public int MinBars => Math.Max(Math.Max(SlowSmaDays, RegimeSmaDays), Math.Max(BreakoutDays, AtrDays) + 1);

    /// <summary>Throws when a setting is out of range, so a bad configuration fails at startup rather than advising nonsense.</summary>
    public AdvisorSettings Validate()
    {
        if (!StockSymbol.IsValid(BenchmarkSymbol))
            throw new ArgumentException($"Benchmark symbol '{BenchmarkSymbol}' isn't TICKER.EXCHANGE.");
        Positive(RegimeSmaDays, nameof(RegimeSmaDays));
        Positive(FastSmaDays, nameof(FastSmaDays));
        Positive(SlowSmaDays, nameof(SlowSmaDays));
        Positive(BreakoutDays, nameof(BreakoutDays));
        Positive(AtrDays, nameof(AtrDays));
        Positive(MomentumDays, nameof(MomentumDays));
        Positive(MaxPositions, nameof(MaxPositions));
        Positive(MaxBarAgeDays, nameof(MaxBarAgeDays));
        if (FastSmaDays >= SlowSmaDays)
            throw new ArgumentException($"{nameof(FastSmaDays)} must be shorter than {nameof(SlowSmaDays)}.");
        Between(InitialStopAtr, 0.5m, 10m, nameof(InitialStopAtr));
        Between(TrailingStopAtr, 0.5m, 10m, nameof(TrailingStopAtr));
        Between(RiskOffTrailingStopAtr, 0.5m, TrailingStopAtr, nameof(RiskOffTrailingStopAtr));
        Between(GapAtr, 0m, 5m, nameof(GapAtr));
        Between(RiskPerTrade, 0.001m, 0.05m, nameof(RiskPerTrade));
        Between(MaxPositionFraction, 0.01m, 1m, nameof(MaxPositionFraction));
        Between(MinStopRaiseFraction, 0m, 0.1m, nameof(MinStopRaiseFraction));
        return this;
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
    }

    private static void Between(decimal value, decimal min, decimal max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Must be between {min} and {max}.");
    }
}
