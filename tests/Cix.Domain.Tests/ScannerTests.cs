using Cix.Domain.Exchange;
using Cix.Domain.MarketData;
using Cix.Domain.Scanning;

namespace Cix.Domain.Tests;

public sealed class ScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 30, TimeSpan.Zero);
    private static readonly ScannerParameters Defaults = new();

    private static PairInfo Pair(string symbol, bool active = true, decimal minQuote = 1m) =>
        new(symbol, symbol[..symbol.IndexOf('-', StringComparison.Ordinal)], "EUR", 0.0001m, 0.01m, 0.0001m, 1000m, minQuote, active);

    /// <summary>24 hourly candles ending at 11:00, closing at 100 with <paramref name="volume"/> each (240,000 EUR a day at 100).</summary>
    private static List<Candle> Hourly(int count = 24, decimal volume = 100m, decimal lastVolume = -1m) =>
        Enumerable.Range(0, count)
            .Select(i => new Candle(new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero).AddHours(i - count + 1), 100m, 101m, 99m, 100m,
                i == count - 1 && lastVolume >= 0 ? lastVolume : volume))
            .ToList();

    /// <summary>
    /// A healthy uptrend: price 100 (bid 99.95, ask 100.05, spread 0.001), EMA20 99.8, ATR 1.5 (0.015), 1h +0.2%, 24h +3%.
    /// Round-trip cost 2 × 0.0009 + 0.001 + 2 × 0.0005 = 0.0038, well under the ATR.
    /// </summary>
    private static FeatureSet Features(
        string symbol = "SOL-EUR",
        Trend trend = Trend.Up,
        decimal price = 100m,
        decimal ema20 = 99.8m,
        decimal atrFraction = 0.015m,
        decimal spread = 0.001m,
        decimal? return1h = 0.002m,
        decimal? return24h = 0.03m,
        bool stale = false,
        decimal bid = 99.95m) =>
        new(symbol, Now, stale, price, bid, 100.05m, spread, trend, ema20, 98m, atrFraction * price, atrFraction, return1h, return24h,
            102m, 97m, -0.02m, 0.03m, 0.1m, 60);

    private static ScanInput Input(
        FeatureSet? features = null,
        List<Candle>? hourly = null,
        PairInfo? pair = null,
        bool allowed = true,
        bool listed = true,
        bool openOrder = false)
    {
        var f = features ?? Features();
        return new ScanInput(f.Symbol, f, hourly ?? Hourly(), listed ? pair ?? Pair(f.Symbol) : null, allowed, openOrder);
    }

    private static ScanResult Scan(ScanInput[] inputs, ScannerParameters? parameters = null, IReadOnlyList<HeldPosition>? held = null, bool allowEntries = true) =>
        MarketScanner.Scan(inputs, held ?? [], parameters ?? Defaults, Now, allowEntries);

    [Fact]
    public void RoundTripCost_IsTwoFeesPlusSpreadPlusTwoSlippages() =>
        Assert.Equal(0.0038m, MarketScanner.RoundTripCost(0.001m, Defaults));

    [Fact]
    public void HealthyUptrendPullback_IsRanked_WithASizedEntry()
    {
        var result = Scan([Input()]);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(("SOL-EUR", 1, Trend.Up), (row.Symbol, row.Rank, row.Trend));
        Assert.Equal(240_000m, row.QuoteVolume24h);
        Assert.Equal(1m, row.VolumeRatio);
        Assert.Equal(0.0038m, row.RoundTripCostFraction);
        // momentum 0.03 / 0.015 = 2, trend +1, volume 0, cost 2 × 0.0038 / 0.015 = 0.50667
        Assert.Equal(2.493m, row.Score);
        Assert.Null(row.NoEntryReason);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(100.05m, entry.LimitPrice);
        Assert.Equal(97.80m, entry.StopLoss);      // 100.05 - 1.5 × 1.5, down to the 0.01 tick
        Assert.Equal(104.55m, entry.TakeProfit);   // 100.05 + 2 × 2.25
        // Risk sizing allows 1 / (2.25 + 0.0038 × 100.05) = 0.38 units; the 5 EUR order cap allows 0.04997, floored to 0.0001.
        Assert.Equal(0.0499m, entry.BaseSize);
        Assert.Equal(4.99m, entry.QuoteSize);
        Assert.Equal(0.13m, entry.RiskQuote);      // 0.0499 × 2.63019
        Assert.Empty(result.Excluded);
        Assert.Empty(result.Exits);
    }

    [Fact]
    public void RiskPerTrade_CapsTheSize_IncludingCosts()
    {
        var result = Scan([Input()], new ScannerParameters { RiskPerTradeQuote = 0.05m });

        var entry = Assert.Single(result.Entries);
        Assert.Equal(0.019m, entry.BaseSize);      // 0.05 / 2.63019 = 0.01901, floored
        Assert.True(entry.RiskQuote <= 0.05m);
        Assert.Equal(1.90m, entry.QuoteSize);
    }

    public static TheoryData<string, string> ExcludedCases => new()
    {
        { "not-allowed", "SOL-EUR" },
        { "not-listed", "SOL-EUR" },
        { "inactive", "SOL-EUR" },
        { "stale", "SOL-EUR" },
        { "insufficient-history", "SOL-EUR" },
        { "low-volume", "SOL-EUR" },
        { "wide-spread", "SOL-EUR" },
        { "too-volatile", "SOL-EUR" },
        { "costs-exceed-volatility", "SOL-EUR" },
    };

    [Theory]
    [MemberData(nameof(ExcludedCases))]
    public void FailingSymbols_NeverBecomeCandidates(string reason, string symbol)
    {
        var input = reason switch
        {
            "not-allowed" => Input(allowed: false),
            "not-listed" => Input(listed: false),
            "inactive" => Input(pair: Pair(symbol, active: false)),
            "stale" => Input(Features(stale: true)),
            "insufficient-history" => Input(hourly: Hourly(count: 5)),
            "low-volume" => Input(hourly: Hourly(volume: 1m)),            // 2,400 EUR a day
            "wide-spread" => Input(Features(spread: 0.004m)),
            "too-volatile" => Input(Features(atrFraction: 0.08m)),
            "costs-exceed-volatility" => Input(Features(atrFraction: 0.005m)), // under 2 × 0.0038
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        var result = Scan([input]);

        var excluded = Assert.Single(result.Excluded);
        Assert.Equal((symbol, reason), (excluded.Symbol, excluded.Reason));
        Assert.Empty(result.Ranked);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void FlatMarket_WithZeroAtr_IsExcluded_NotADivideByZero()
    {
        var result = Scan([Input(Features(atrFraction: 0m))]);

        Assert.Equal("insufficient-history", Assert.Single(result.Excluded).Reason);
        Assert.Throws<InvalidOperationException>((Defaults with { MinAtrToCostRatio = 0m }).Validate);
    }

    [Fact]
    public void Ranking_PrefersStrongerRiskAdjustedMomentum_AndHonoursTheSignalLimit()
    {
        var result = Scan(
            [
                Input(Features("LINK-EUR", return24h: 0.01m)),
                Input(Features("SOL-EUR", return24h: 0.06m)),
                Input(Features("AVAX-EUR", return24h: 0.03m), Hourly(lastVolume: 300m)), // unusual volume in the last hour
            ],
            new ScannerParameters { MaxEntrySignals = 1 });

        Assert.Equal(new[] { "SOL-EUR", "AVAX-EUR", "LINK-EUR" }, result.Ranked.Select(r => r.Symbol).ToArray());
        Assert.Equal("SOL-EUR", Assert.Single(result.Entries).Symbol);
        Assert.Equal("signal limit reached", result.Ranked[1].NoEntryReason);
    }

    [Fact]
    public void VolumeSpike_RaisesTheScore()
    {
        var plain = Scan([Input()]).Ranked[0].Score;
        var spiking = Scan([Input(hourly: Hourly(lastVolume: 300m))]).Ranked[0].Score;

        Assert.True(spiking > plain);
    }

    [Theory]
    [InlineData(Trend.Flat, 99.8, 0.002, "trend flat")]
    [InlineData(Trend.Down, 99.8, 0.002, "trend down")]
    [InlineData(Trend.Up, 98.5, 0.002, "extended: 1 ATR above EMA20")]   // (100 - 98.5) / 1.5
    [InlineData(Trend.Up, 99.8, -0.001, "last hour falling")]
    public void NoEntry_WithoutAnUptrendPullback(Trend trend, double ema20, double return1h, string why)
    {
        var result = Scan([Input(Features(trend: trend, ema20: (decimal)ema20, return1h: (decimal)return1h))]);

        Assert.Empty(result.Entries);
        Assert.Equal(why, Assert.Single(result.Ranked).NoEntryReason);
    }

    [Fact]
    public void NoEntry_BelowTheExchangeMinimum()
    {
        var result = Scan([Input(pair: Pair("SOL-EUR", minQuote: 10m))]); // the 5 EUR cap can't reach 10

        Assert.Empty(result.Entries);
        Assert.Equal("below exchange minimum", result.Ranked[0].NoEntryReason);
    }

    [Fact]
    public void NoEntry_WhenHeld_WithAnOpenOrder_OrHalted()
    {
        Assert.Equal("already held", Scan([Input()], held: [new HeldPosition("SOL-EUR", 0.05m, 97m, 110m)]).Ranked[0].NoEntryReason);
        Assert.Equal("open order", Scan([Input(openOrder: true)]).Ranked[0].NoEntryReason);
        var halted = Scan([Input()], allowEntries: false);
        Assert.Empty(halted.Entries);
        Assert.Equal("trading halted", halted.Ranked[0].NoEntryReason);
    }

    [Theory]
    [InlineData(97.5, 97.5, 110, Trend.Up, "stop")]
    [InlineData(110, 97, 110, Trend.Up, "target")]
    [InlineData(100, 97, 110, Trend.Down, "trend-down")]
    [InlineData(100, 97, 110, Trend.Up, null)]
    public void HeldPositions_ExitOnStopTargetOrDowntrend(double bid, double stop, double target, Trend trend, string? reason)
    {
        var held = new HeldPosition("SOL-EUR", 0.05m, (decimal)stop, (decimal)target);

        var result = Scan([Input(Features(trend: trend, bid: (decimal)bid))], held: [held]);

        if (reason is null)
        {
            Assert.Empty(result.Exits);
            return;
        }

        var exit = Assert.Single(result.Exits);
        Assert.Equal(("SOL-EUR", 0.05m, (decimal)bid, reason), (exit.Symbol, exit.BaseSize, exit.LimitPrice, exit.Reason));
    }

    [Fact]
    public void HeldPosition_ExitsEvenWhenTheSymbolFailsTheEntryFilters()
    {
        var held = new HeldPosition("SOL-EUR", 0.05m, 98m, 110m);

        var result = Scan([Input(Features(spread: 0.004m, bid: 97.9m))], held: [held]);

        Assert.Equal("wide-spread", Assert.Single(result.Excluded).Reason);
        Assert.Equal("stop", Assert.Single(result.Exits).Reason);
    }

    [Fact]
    public void HeldPosition_WithStaleData_GetsNoExitSignal()
    {
        var held = new HeldPosition("SOL-EUR", 0.05m, 98m, 110m);

        var result = Scan([Input(Features(stale: true, bid: 90m))], held: [held]);

        Assert.Empty(result.Exits);
        Assert.Equal("stale", Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void HeldPosition_WithoutAPlan_ExitsOnlyOnADowntrend()
    {
        var held = new HeldPosition("SOL-EUR", 0.05m, null, null);

        Assert.Empty(Scan([Input(Features(bid: 50m))], held: [held]).Exits);
        Assert.Equal("trend-down", Assert.Single(Scan([Input(Features(trend: Trend.Down))], held: [held]).Exits).Reason);
    }

    [Theory]
    [InlineData("MaxSpreadFraction")]
    [InlineData("StopAtr")]
    [InlineData("OrderQuote")]
    [InlineData("CostWeight")]
    public void Parameters_RejectNonsense(string field)
    {
        var bad = field switch
        {
            "MaxSpreadFraction" => Defaults with { MaxSpreadFraction = 0m },
            "StopAtr" => Defaults with { StopAtr = -1m },
            "OrderQuote" => Defaults with { OrderQuote = 0m },
            _ => Defaults with { CostWeight = -1m },
        };

        Assert.Throws<InvalidOperationException>(bad.Validate);
        Defaults.Validate();
    }
}
