using Cix.Domain.Exchange;
using Cix.Domain.MarketData;
using Cix.Domain.Scanning;

namespace Cix.Domain.Tests;

/// <summary>The daily swing rules (ADR-0028): breakout entry, BTC regime, daily trend exit and the trailing stop.</summary>
public sealed class DailyScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 30, TimeSpan.Zero);

    /// <summary>swing.v1's scanner settings.</summary>
    private static readonly ScannerParameters Swing = new()
    {
        Timeframe = ScannerParameters.Daily,
        FastEmaPeriod = 10,
        SlowEmaPeriod = 50,
        RegimeEmaPeriod = 100,
        BreakoutCandles = 20,
        EntryMaxAboveBreakoutAtr = 0.5m,
        MinQuoteVolume24h = 20_000m,
        MaxSpreadFraction = 0.003m,
        MaxAtrFraction = 0.15m,
        MinAtrToCostRatio = 3m,
        StopAtr = 3m,
        RewardRisk = 0m,
        TrailAtr = 3m,
        RiskPerTradeQuote = 4m,
        OrderQuote = 50m,
        MaxEntrySignals = 3,
        TrendWeight = 0m,
        VolumeWeight = 0m,
        CostWeight = 0m,
    };

    private static PairInfo Pair(string symbol) =>
        new(symbol, symbol[..symbol.IndexOf('-', StringComparison.Ordinal)], "EUR", 0.0001m, 0.01m, 0.0001m, 1000m, 1m, true);

    /// <summary><paramref name="count"/> daily candles, the newest from yesterday (closed at midnight UTC), from (close, high, low) per index.</summary>
    private static List<Candle> Daily(int count, Func<int, (decimal Close, decimal High, decimal Low)> at, decimal volume = 1_000m, int endDaysAgo = 1)
    {
        var newest = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-endDaysAgo);
        return Enumerable.Range(0, count).Select(i =>
        {
            var (close, high, low) = at(i);
            return new Candle(newest.AddDays(i - count + 1), close, high, low, close, volume);
        }).ToList();
    }

    /// <summary>
    /// SOL: 110 days rising 0.4 a day from 50 (to 93.6), nine flat days at 93, then a close at 97: above the 20-day high of
    /// 94.6 (day 109's), in a daily uptrend. Every candle spans 2 EUR, so the daily ATR is about 2% of the price.
    /// </summary>
    private static List<Candle> SolBreakout(decimal lastClose = 97m) => Daily(120, i => i switch
    {
        < 110 => (50m + (0.4m * i), 51m + (0.4m * i), 49m + (0.4m * i)),
        < 119 => (93m, 94.5m, 92m),
        _ => (lastClose, lastClose + 0.5m, 92m),
    });

    /// <summary>BTC rising 150 a day from 40,000: its close is far above its EMA 100.</summary>
    private static List<Candle> BtcUp() => Daily(120, i => (40_000m + (150m * i), 40_600m + (150m * i), 39_400m + (150m * i)));

    /// <summary>BTC rising for 100 days, then falling 600 a day: its last close (37,900) is below its EMA 100.</summary>
    private static List<Candle> BtcDown() => Daily(120, i =>
    {
        var close = i < 100 ? 40_000m + (100m * i) : 49_900m - (600m * (i - 99));
        return (close, close + 600m, close - 600m);
    });

    private static FeatureSet Quote(string symbol, decimal bid, decimal ask, Trend hourlyTrend = Trend.Up) =>
        new(symbol, Now, false, (bid + ask) / 2, bid, ask, Math.Round((ask - bid) / ((bid + ask) / 2), 6), hourlyTrend, null, null, null, null,
            0.001m, 0.02m, null, null, null, null, null, 60);

    private static ScanInput Sol(List<Candle>? daily = null, decimal bid = 96.95m, decimal ask = 97.05m, Trend hourlyTrend = Trend.Up) =>
        new("SOL-EUR", Quote("SOL-EUR", bid, ask, hourlyTrend), [], Pair("SOL-EUR"), true, false, daily ?? SolBreakout());

    private static ScanInput Btc(List<Candle>? daily = null)
    {
        var candles = daily ?? BtcUp();
        var close = candles[^1].Close;
        return new ScanInput("BTC-EUR", Quote("BTC-EUR", close - 5m, close + 5m), [], Pair("BTC-EUR"), true, false, candles);
    }

    private static ScanResult Scan(ScanInput[] inputs, IReadOnlyList<HeldPosition>? held = null, ScannerParameters? parameters = null) =>
        MarketScanner.Scan(inputs, held ?? [], parameters ?? Swing, Now);

    private static decimal Floor(decimal value, decimal step) => Math.Floor(value / step) * step;

    [Fact]
    public void DailyBreakout_InAnUptrend_WithBtcAboveItsEma_IsABuyWithAStopAndNoTarget()
    {
        var result = Scan([Sol(), Btc()]);

        var atr = Indicators.Atr(SolBreakout(), 14)!.Value;
        var entry = Assert.Single(result.Entries, e => e.Symbol == "SOL-EUR");
        Assert.Equal(97.05m, entry.LimitPrice);
        Assert.Equal(Floor(97.05m - (3m * atr), 0.01m), entry.StopLoss); // 3 daily ATRs below the ask
        Assert.Null(entry.TakeProfit);
        Assert.True(entry.QuoteSize <= 50m && entry.RiskQuote <= 4m, $"{entry.QuoteSize} EUR, {entry.RiskQuote} at risk");
        Assert.StartsWith("20-day breakout: close +2.54% over the 20-day high", entry.Reason, StringComparison.Ordinal);

        var row = Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR");
        Assert.Equal(Math.Round(atr / 97m, 6), row.AtrFraction); // the daily ATR, not the hourly one
        Assert.Equal(Trend.Up, row.Trend);
        Assert.Equal(93_571m, row.QuoteVolume24h); // the last seven days of 1,000 units: six at 93 and one at 97, averaged
    }

    [Fact]
    public void WithoutABreakout_TheCoinIsRankedWithoutAnEntry()
    {
        var result = Scan([Sol(SolBreakout(lastClose: 94m), bid: 93.95m, ask: 94.05m), Btc()]);

        var row = Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR");
        Assert.Equal("no breakout", row.NoEntryReason);
        Assert.Equal("The last daily close is 0.63% below the highest high of the 20 days before it; the strategy buys a close above it.", row.NoEntryDetail);
        Assert.DoesNotContain(result.Entries, e => e.Symbol == "SOL-EUR");
    }

    [Fact]
    public void BtcBelowItsEma_OrNotWatched_HoldsEveryBuy()
    {
        var down = Scan([Sol(), Btc(BtcDown())]);
        var row = Assert.Single(down.Ranked, r => r.Symbol == "SOL-EUR");
        Assert.Equal("regime: BTC below EMA100", row.NoEntryReason);
        Assert.StartsWith("BTC-EUR's last daily close is ", row.NoEntryDetail, StringComparison.Ordinal);
        Assert.Empty(down.Entries);

        var missing = Scan([Sol()]);
        Assert.Equal("regime unknown", Assert.Single(missing.Ranked).NoEntryReason);
        Assert.Empty(missing.Entries);

        var off = Scan([Sol()], parameters: Swing with { RegimeEmaPeriod = 0 });
        Assert.Single(off.Entries); // no regime filter: BTC isn't needed
    }

    [Fact]
    public void TheRegimeHoldsBuys_EvenWithoutTheBreakoutRule()
    {
        var result = Scan([Sol(), Btc(BtcDown())], parameters: Swing with { RequireUptrendPullback = false });

        Assert.Equal("regime: BTC below EMA100", Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR").NoEntryReason);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ACoinSoldSinceTheLastDailyCandleBegan_IsNotBoughtBackOnTheSameBreakout()
    {
        // The last daily candle began 2026-10-22 00:00 UTC: a stop-out today waits for the next close.
        var soldToday = Scan([Sol() with { LastSoldAt = Now.AddHours(-3) }, Btc()]);
        Assert.Equal("sold since the breakout", Assert.Single(soldToday.Ranked, r => r.Symbol == "SOL-EUR").NoEntryReason);
        Assert.Empty(soldToday.Entries);

        var soldBefore = Scan([Sol() with { LastSoldAt = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero) }, Btc()]);
        Assert.Single(soldBefore.Entries); // sold before this breakout's day: a new signal
    }

    [Theory]
    [InlineData(98, true)] // the highest high just before the 20-day window doesn't count
    [InlineData(99, false)] // the first day of the window does
    public void TheBreakoutWindow_IsTheTwentyDaysBeforeTheLastClose(int index, bool breakout)
    {
        var candles = SolBreakout();
        candles[index] = candles[index] with { High = 98m };

        var result = Scan([Sol(candles), Btc()]);

        Assert.Equal(breakout, result.Entries.Count == 1);
        if (!breakout)
            Assert.Equal("no breakout", Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR").NoEntryReason);
    }

    [Fact]
    public void AnAskFarAboveTheBreakoutClose_IsNotChased()
    {
        var result = Scan([Sol(bid: 99.95m, ask: 100.05m), Btc()]);

        var row = Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR");
        Assert.StartsWith("extended: ", row.NoEntryReason, StringComparison.Ordinal);
        Assert.EndsWith("ATR above the breakout close", row.NoEntryReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAskBackUnderTheBrokenHigh_IsNoBuy()
    {
        var result = Scan([Sol(bid: 94.45m, ask: 94.55m), Btc()]); // the 20-day high was 94.6

        Assert.Equal("back below the breakout", Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR").NoEntryReason);
    }

    [Fact]
    public void NoDailyUptrend_NoEntry()
    {
        // Falling 0.5 a day, then a last close above the previous 20 days' highs: a breakout, but below EMA 50.
        var falling = Daily(120, i => i < 119 ? (150m - (0.5m * i), 150.5m - (0.5m * i), 149m - (0.5m * i)) : (102m, 102.5m, 90m));
        var result = Scan([Sol(falling, bid: 101.95m, ask: 102.05m), Btc()]);

        Assert.Equal("no daily uptrend", Assert.Single(result.Ranked, r => r.Symbol == "SOL-EUR").NoEntryReason);
    }

    [Fact]
    public void MissingOrBehindDailyCandles_ExcludeTheCoin()
    {
        var result = Scan([Sol(SolBreakout().TakeLast(30).ToList()), Btc(), Sol(SolBreakout()) with { Symbol = "ADA-EUR", Daily = Daily(120, i => (97m, 98m, 96m), endDaysAgo: 3) }]);

        var few = Assert.Single(result.Excluded, x => x.Symbol == "SOL-EUR");
        Assert.Equal(("insufficient-history", "Only 30 daily candles; 50 needed."), (few.Reason, few.Detail));
        Assert.Equal("stale", Assert.Single(result.Excluded, x => x.Symbol == "ADA-EUR").Reason);
    }

    [Fact]
    public void HeldCoin_LeavesOnADailyCloseBelowTheSlowEma_NotOnTheHourlyTrend()
    {
        // Rising for 100 days, then falling 1 a day for 20: the last close (69.6) is below EMA 50.
        var weak = Daily(120, i => i < 100 ? (50m + (0.4m * i), 51m + (0.4m * i), 49m + (0.4m * i)) : (89.6m - (i - 99), 90.6m - (i - 99), 88.6m - (i - 99)));
        var held = new[] { new HeldPosition("SOL-EUR", 0.3m, 60m, null, Now.AddDays(-30)) };

        var exit = Assert.Single(Scan([Sol(weak, bid: 68.95m, ask: 69.05m, hourlyTrend: Trend.Up), Btc()], held).Exits);
        Assert.Equal(("trend-down", 0.3m, 68.95m), (exit.Reason, exit.BaseSize, exit.LimitPrice));
        Assert.StartsWith("the daily close fell ", exit.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" below EMA50", exit.Detail, StringComparison.Ordinal);

        // A daily uptrend with an hourly downtrend: no trend exit (the hourly trend isn't the strategy's).
        Assert.Empty(Scan([Sol(hourlyTrend: Trend.Down), Btc()], held).Exits);
        // Its stop still applies.
        Assert.Equal("stop", Assert.Single(Scan([Sol(bid: 59.95m, ask: 60.05m), Btc()], held).Exits).Reason);
    }

    [Fact]
    public void HeldStop_TrailsTheHighestDailyCloseSinceTheBuy_OnlyUpward()
    {
        var daily = SolBreakout();
        var atr = Indicators.Atr(daily, 14)!.Value;
        var filled = Now.AddDays(-15); // 2026-10-08 12:00: the candles from 10-08 on closed after it, the highest at 97
        var expected = Floor(97m - (3m * atr), 0.01m);

        var raise = Assert.Single(Scan([Sol(), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, filled)]).StopRaises!);
        Assert.Equal(("SOL-EUR", 80m, expected), (raise.Symbol, raise.From, raise.To));
        Assert.Equal("trailing stop: highest daily close since the buy less 3 daily ATR", raise.Detail);

        Assert.Empty(Scan([Sol(), Btc()], [new HeldPosition("SOL-EUR", 0.3m, expected + 1m, null, filled)]).StopRaises!); // never lowered
        Assert.Empty(Scan([Sol(), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, null)]).StopRaises!); // fill time unknown
        Assert.Empty(Scan([Sol(), Btc()], [new HeldPosition("SOL-EUR", 0.3m, null, null, filled)]).StopRaises!); // no stop to raise
        Assert.Empty(Scan([Sol(), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, filled)], Swing with { TrailAtr = 0m }).StopRaises!);
        Assert.Empty(Scan([Sol(bid: 79.95m, ask: 80.05m), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, filled)]).StopRaises!); // being sold at its stop

        // A bid under the trail level: the raise stops 0.5% under the bid, so the raise alone never sells.
        var under = Assert.Single(Scan([Sol(bid: 85m, ask: 85.1m), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, filled)]).StopRaises!);
        Assert.Equal(84.57m, under.To); // 85 × 0.995 = 84.575, floored to the 0.01 step
        Assert.Empty(Scan([Sol(bid: 80.2m, ask: 80.3m), Btc()], [new HeldPosition("SOL-EUR", 0.3m, 80m, null, filled)]).StopRaises!); // 79.79 is no raise
    }

    [Fact]
    public void HourlyStrategy_WithoutATarget_BuysWithAStopOnly()
    {
        var features = new FeatureSet("SOL-EUR", Now, false, 100m, 99.95m, 100.05m, 0.001m, Trend.Up, 99.8m, 98m, 1.5m, 0.015m, 0.002m, 0.03m,
            102m, 97m, -0.02m, 0.03m, 0.1m, 60);
        var hourly = Enumerable.Range(0, 24).Select(i => new Candle(new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero).AddHours(i - 23), 100m, 101m, 99m, 100m, 100m)).ToList();
        var result = MarketScanner.Scan([new ScanInput("SOL-EUR", features, hourly, Pair("SOL-EUR"), true, false)], [], new ScannerParameters { RewardRisk = 0m }, Now);

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.TakeProfit);
        Assert.Equal(97.8m, entry.StopLoss); // 1.5 ATR below the ask, as with a target
        Assert.Empty(result.StopRaises ?? []);
    }

    [Theory]
    [InlineData("1h", 20, 50, 0, 0, true)]
    [InlineData("1d", 10, 50, 100, 3, true)]
    [InlineData("1d", 20, 150, 150, 0, true)]
    [InlineData("1h", 20, 100, 0, 0, false)]   // hourly EMAs stop at 55
    [InlineData("1h", 20, 50, 100, 0, false)]  // the regime filter is daily only
    [InlineData("1h", 20, 50, 0, 3, false)]    // so is the trailing stop
    [InlineData("1d", 20, 151, 0, 0, false)]
    [InlineData("1d", 10, 50, 1, 0, false)]    // an EMA of one day is no filter
    [InlineData("4h", 20, 50, 0, 0, false)]
    [InlineData("1d", 10, 50, 100, 1, true)]
    [InlineData("1d", 10, 50, 100, 11, false)]
    public void Validate_TheTimeframeDecidesWhatIsAllowed(string timeframe, int fast, int slow, int regime, int trail, bool valid)
    {
        var p = new ScannerParameters { Timeframe = timeframe, FastEmaPeriod = fast, SlowEmaPeriod = slow, RegimeEmaPeriod = regime, TrailAtr = trail, RewardRisk = 0m };
        if (valid)
            p.Validate();
        else
            Assert.Throws<InvalidOperationException>(p.Validate);
    }
}
