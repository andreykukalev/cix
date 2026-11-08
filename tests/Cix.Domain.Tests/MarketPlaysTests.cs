using Cix.Domain.Exchange;
using Cix.Domain.MarketData;
using Cix.Domain.Scanning;

namespace Cix.Domain.Tests;

/// <summary>The plays of ADR-0031: a volume burst on Binance's hourly candles, and the baskets bought after a sharp BTC move.</summary>
public sealed class MarketPlaysTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScannerParameters Costs = new() { MaxSpreadFraction = 0.003m };

    /// <summary>swing.v3's plays.</summary>
    private static readonly BurstPlay BurstV3 = new();
    private static readonly EventPlay RallyV3 = new() { BtcMoveFraction = 0.13m, StopFraction = 0.12m, HoldDays = 30, Basket = ["SOL-EUR", "ADA-EUR"] };
    private static readonly EventPlay CrashV3 = new() { BtcMoveFraction = -0.15m, StopFraction = 0.25m, HoldDays = 14, Basket = ["SOL-EUR", "ADA-EUR"] };

    private static PairInfo Pair(string symbol) =>
        new(symbol, symbol[..symbol.IndexOf('-', StringComparison.Ordinal)], "EUR", 0.0001m, 0.01m, 0.0001m, 1000m, 1m, true);

    private static ScanInput Coin(string symbol, decimal bid = 101.95m, decimal ask = 102.05m, bool openOrder = false, DateTimeOffset? soldAt = null) =>
        new(symbol, new FeatureSet(symbol, Now, false, (bid + ask) / 2, bid, ask, Math.Round((ask - bid) / ((bid + ask) / 2), 6), Trend.Up,
            null, null, null, null, null, null, null, null, null, null, null, 60), [], Pair(symbol), true, openOrder, null, soldAt);

    /// <summary>
    /// 200 hourly candles, the newest the hour to 12:00 that just closed: 199 flat ones (close 100, high 101, low 99, volume 100)
    /// and a last one closing at <paramref name="lastClose"/> on <paramref name="lastVolume"/>.
    /// </summary>
    private static List<Candle> Hourly(decimal lastClose = 102m, decimal lastVolume = 500m, int endHoursAgo = 0)
    {
        var newest = new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero).AddHours(-endHoursAgo);
        return Enumerable.Range(0, 200).Select(i => i < 199
            ? new Candle(newest.AddHours(i - 199), 100m, 101m, 99m, 100m, 100m)
            : new Candle(newest, 100m, lastClose + 0.5m, 99.5m, lastClose, lastVolume)).ToList();
    }

    /// <summary>120 BTC daily candles, the newest yesterday's (closed at midnight UTC), from the close per index.</summary>
    private static List<Candle> Btc(Func<int, decimal> close) =>
        Enumerable.Range(0, 120).Select(i => new Candle(Midnight.AddDays(i - 120), close(i), close(i) + 500m, close(i) - 500m, close(i), 1_000m)).ToList();

    /// <summary>BTC rising 150 a day: above its EMA100, and up less than 1% over three days.</summary>
    private static List<Candle> BtcUp() => Btc(i => 40_000m + (150m * i));

    private static PlayResult Evaluate(
        PlayParameters p, IReadOnlyList<ScanInput> inputs, IReadOnlyDictionary<string, IReadOnlyList<Candle>>? hourly = null, IReadOnlyList<Candle>? btc = null,
        IReadOnlyList<HeldPosition>? held = null, DateTimeOffset? now = null, string? paused = null) =>
        MarketPlays.Evaluate(inputs, hourly ?? new Dictionary<string, IReadOnlyList<Candle>>(), btc ?? BtcUp(), held ?? [], p, 250m, Costs, now ?? Now, paused);

    private static Dictionary<string, IReadOnlyList<Candle>> Candles(params (string Symbol, List<Candle> Hourly)[] coins) =>
        coins.ToDictionary(c => c.Symbol, c => (IReadOnlyList<Candle>)c.Hourly, StringComparer.Ordinal);

    private static decimal Floor(decimal value, decimal step) => Math.Floor(value / step) * step;

    [Fact]
    public void Burst_OnABreakoutWithFourTimesTheVolume_WhileBtcIsUp_IsABuyOfSixPercent_WithAStopAndATimeToSell()
    {
        var hourly = Hourly();
        var result = Evaluate(new PlayParameters { Burst = BurstV3 }, [Coin("SOL-EUR")], Candles(("SOL-EUR", hourly)));

        var entry = Assert.Single(result.Entries);
        var atr = Indicators.Atr(hourly, 14)!.Value;
        Assert.Equal(("SOL-EUR", 1, MarketPlays.Burst, 102.05m), (entry.Symbol, entry.Rank, entry.Play, entry.LimitPrice));
        // 6% of 250 is 15.00: 0.1469 SOL at 102.05.
        Assert.Equal((0.1469m, 14.99m), (entry.BaseSize, entry.QuoteSize));
        Assert.Equal(Floor(102.05m * (1 - (4m * atr / 102m)), 0.01m), entry.StopLoss);
        Assert.Null(entry.TakeProfit);
        Assert.Equal(Now.AddHours(72), entry.ExitBy);
        Assert.StartsWith("volume burst: the hour to 12:00 UTC closed above the 96-hour high on 5x the average volume (Binance)", entry.Reason, StringComparison.Ordinal);
        Assert.Equal(new PlayStatus(MarketPlays.Burst, "signal", "1 burst(s) this hour."), Assert.Single(result.Statuses));
    }

    [Fact]
    public void NoBurst_WithoutTheVolume_TheBreakout_AFreshHour_OrABuyablePrice()
    {
        var burst = new PlayParameters { Burst = BurstV3 };
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly(lastVolume: 390m)))).Entries); // 3.9x
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly(lastClose: 101m)))).Entries); // not above the 101 high
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly(endHoursAgo: 1)))).Entries); // closed an hour ago
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly().TakeLast(150).ToList()))).Entries); // too few hours
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")]).Entries); // no Binance candles for it
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR", bid: 101.6m, ask: 102.05m)], Candles(("SOL-EUR", Hourly()))).Entries); // spread 0.44%
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR", openOrder: true)], Candles(("SOL-EUR", Hourly()))).Entries);
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR", bid: 50.98m, ask: 51.02m)], Candles(("SOL-EUR", Hourly()))).Entries); // Binance's coin costs twice: not the same one
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR", soldAt: Now.AddMinutes(-30))], Candles(("SOL-EUR", Hourly()))).Entries); // sold in the burst hour
        Assert.Single(Evaluate(burst, [Coin("SOL-EUR", soldAt: Now.AddHours(-2))], Candles(("SOL-EUR", Hourly()))).Entries); // sold before it
        Assert.Empty(Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), held: [new HeldPosition("SOL-EUR", 1m, 90m, null, Now.AddDays(-3))]).Entries);

        var quiet = Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly(lastVolume: 390m))));
        Assert.Equal(new PlayStatus(MarketPlays.Burst, "waiting", "No coin's last hour closed above its 96-hour high on 4x its average volume (Binance)."), Assert.Single(quiet.Statuses));
    }

    [Fact]
    public void Bursts_WaitWhileBtcIsBelowItsEma_OrItsCandlesAreBehind()
    {
        var burst = new PlayParameters { Burst = BurstV3 };
        var down = Btc(i => i < 100 ? 40_000m + (100m * i) : 49_900m - (600m * (i - 99)));

        var below = Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), down);
        Assert.Empty(below.Entries);
        var status = Assert.Single(below.Statuses);
        Assert.Equal("waiting", status.State);
        Assert.EndsWith("below its EMA100; bursts are bought only while BTC is above it.", status.Detail, StringComparison.Ordinal);

        var behind = Evaluate(burst, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), BtcUp().SkipLast(2).ToList());
        Assert.Empty(behind.Entries);
        Assert.StartsWith("Bursts wait for BTC's daily trend on Binance", Assert.Single(behind.Statuses).Detail, StringComparison.Ordinal);

        // Without a regime filter the burst is bought whatever BTC does.
        Assert.Single(Evaluate(new PlayParameters { Burst = BurstV3 with { RegimeEmaPeriod = 0 } }, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), down).Entries);
    }

    [Fact]
    public void Bursts_StopAtTheirMostPositions_TheBiggestVolumeFirst()
    {
        var coins = new[] { Coin("ADA-EUR"), Coin("SOL-EUR"), Coin("XRP-EUR") };
        var hourly = Candles(("ADA-EUR", Hourly(lastVolume: 450m)), ("SOL-EUR", Hourly(lastVolume: 800m)), ("XRP-EUR", Hourly(lastVolume: 600m)));
        var heldBurst = new HeldPosition("DOGE-EUR", 10m, 0.1m, null, Now.AddHours(-5), MarketPlays.Burst, Now.AddHours(67));

        var two = Evaluate(new PlayParameters { Burst = BurstV3 with { MaxPositions = 3 } }, coins, hourly, held: [heldBurst]);
        Assert.Equal(new[] { "SOL-EUR", "XRP-EUR" }, two.Entries.Select(e => e.Symbol));
        Assert.Equal(new[] { 1, 2 }, two.Entries.Select(e => e.Rank));

        var full = Evaluate(new PlayParameters { Burst = BurstV3 with { MaxPositions = 1 } }, coins, hourly, held: [heldBurst]);
        Assert.Empty(full.Entries);
        Assert.Equal(new PlayStatus(MarketPlays.Burst, "full", "1 burst positions held, the most the play allows."), Assert.Single(full.Statuses));
    }

    [Fact]
    public void Rally_OnTheFirstDailyCloseThirteenPercentUpInThreeDays_BuysTheBasket_HeldThirtyDays()
    {
        var btc = Btc(i => i < 119 ? 50_000m : 57_000m); // +14% over three days, to yesterday's close
        var result = Evaluate(new PlayParameters { Rally = RallyV3 }, [Coin("ADA-EUR", 0.4999m, 0.5001m), Coin("SOL-EUR"), Coin("XRP-EUR")], btc: btc);

        Assert.Equal(new[] { "SOL-EUR", "ADA-EUR" }, result.Entries.Select(e => e.Symbol)); // the basket's order, XRP not in it
        var sol = result.Entries[0];
        Assert.Equal((MarketPlays.Rally, 0.1469m, Floor(102.05m * 0.88m, 0.01m), Now.AddDays(30)), (sol.Play, sol.BaseSize, sol.StopLoss, sol.ExitBy));
        Assert.Null(sol.TakeProfit);
        Assert.Equal(29.994m, result.Entries[1].BaseSize); // 15 EUR of ADA at 0.5001, to the 0.0001 step
        var status = Assert.Single(result.Statuses);
        Assert.Equal(("rally", "signal"), (status.Play, status.State));
        Assert.Equal("BTC +14% over 3 days to the last daily close (the rally play starts at +13%)", status.Detail);
    }

    [Fact]
    public void Rally_IsBoughtOnlyOnItsFirstDay()
    {
        var rally = new PlayParameters { Rally = RallyV3 };
        // Already +14% at the close before: its day was yesterday.
        var second = Evaluate(rally, [Coin("SOL-EUR")], btc: Btc(i => i < 118 ? 50_000m : 57_000m));
        Assert.Empty(second.Entries);
        Assert.EndsWith("it was already there a day earlier, so this move's buys were due then.", Assert.Single(second.Statuses).Detail, StringComparison.Ordinal);

        // A day after the close, the candles still fresh: too late.
        var late = Evaluate(rally, [Coin("SOL-EUR")], btc: Btc(i => i < 119 ? 50_000m : 57_000m), now: Midnight.AddHours(25));
        Assert.Empty(late.Entries);
        Assert.EndsWith("the day to buy on has passed.", Assert.Single(late.Statuses).Detail, StringComparison.Ordinal);

        // +12%: no rally.
        var small = Evaluate(rally, [Coin("SOL-EUR")], btc: Btc(i => i < 119 ? 50_000m : 56_000m));
        Assert.Empty(small.Entries);
        Assert.Equal(new PlayStatus("rally", "waiting", "BTC +12% over 3 days to the last daily close (the rally play starts at +13%)"), Assert.Single(small.Statuses));

        // Candles behind: nothing to judge.
        Assert.Equal("BTC's daily candles from Binance are missing or behind.",
            Assert.Single(Evaluate(rally, [Coin("SOL-EUR")], btc: Btc(i => i < 119 ? 50_000m : 57_000m).SkipLast(2).ToList()).Statuses).Detail);
    }

    [Fact]
    public void Crash_BuysTheBasketWithAWideStop_OneBtcEventAtATime()
    {
        var btc = Btc(i => i < 119 ? 50_000m : 42_000m); // -16%
        var crash = new PlayParameters { Rally = RallyV3, Crash = CrashV3 };

        var result = Evaluate(crash, [Coin("SOL-EUR"), Coin("ADA-EUR", 0.4999m, 0.5001m)], btc: btc);
        Assert.Equal(new[] { "SOL-EUR", "ADA-EUR" }, result.Entries.Select(e => e.Symbol));
        Assert.All(result.Entries, e => Assert.Equal((MarketPlays.Crash, Now.AddDays(14)), (e.Play, e.ExitBy)));
        Assert.Equal(Floor(102.05m * 0.75m, 0.01m), result.Entries[0].StopLoss);
        Assert.Equal(new[] { "waiting", "signal" }, result.Statuses.Select(s => s.State)); // the rally waits, the crash buys

        // A rally's positions still held: the crash waits.
        var rallyHeld = new HeldPosition("XRP-EUR", 10m, 1m, null, Now.AddDays(-10), MarketPlays.Rally, Now.AddDays(20));
        var blocked = Evaluate(crash, [Coin("SOL-EUR"), Coin("ADA-EUR", 0.4999m, 0.5001m)], btc: btc, held: [rallyHeld]);
        Assert.Empty(blocked.Entries);
        Assert.Equal("full", blocked.Statuses.Single(s => s.Play == "crash").State);

        // This crash's own first buy, filled after the close: the rest of the basket is still bought.
        var own = new HeldPosition("SOL-EUR", 0.1469m, 76.53m, null, Now.AddMinutes(-20), MarketPlays.Crash, Now.AddDays(14));
        var rest = Evaluate(crash, [Coin("SOL-EUR"), Coin("ADA-EUR", 0.4999m, 0.5001m)], btc: btc, held: [own]);
        Assert.Equal("ADA-EUR", Assert.Single(rest.Entries).Symbol);

        // A coin sold since the close isn't bought again; no coin left: the play says so.
        var sold = Evaluate(new PlayParameters { Crash = CrashV3 with { Basket = ["SOL-EUR"] } }, [Coin("SOL-EUR", soldAt: Midnight.AddHours(1))], btc: btc);
        Assert.Empty(sold.Entries);
        Assert.Equal("waiting", Assert.Single(sold.Statuses).State);
    }

    [Fact]
    public void APause_StopsEveryPlay_AndSaysWhy()
    {
        var all = new PlayParameters { Burst = BurstV3, Rally = RallyV3, Crash = CrashV3 };
        var result = Evaluate(all, [Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), Btc(i => i < 119 ? 50_000m : 42_000m), paused: "plays' loss stop");

        Assert.Empty(result.Entries);
        Assert.Equal(new[] { "rally", "crash", "burst" }, result.Statuses.Select(s => s.Play));
        Assert.All(result.Statuses, s => Assert.Equal(("paused", "plays' loss stop"), (s.State, s.Detail)));
    }

    [Fact]
    public void ACoinTooSmallForTheExchange_OrAStopTooClose_IsNotBought()
    {
        // 6% of 10 EUR is 0.60, below the 1 EUR minimum.
        var small = MarketPlays.Evaluate([Coin("SOL-EUR")], Candles(("SOL-EUR", Hourly())), BtcUp(), [], new PlayParameters { Burst = BurstV3 }, 10m, Costs, Now, null);
        Assert.Empty(small.Entries);

        // A crash stop of 0.2% is under the least stop distance; the parameters refuse it anyway.
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Crash = CrashV3 with { StopFraction = 0.002m } }.Validate());
    }

    [Theory]
    [InlineData(0, 0.4, true)]
    [InlineData(0.18, 1.2, true)]
    [InlineData(0.18, 0.4, false)]
    public void PlayParameters_CheckTheirShares(double lossStop, double mainExposure, bool throws)
    {
        var p = new PlayParameters { LossStopFraction = (decimal)lossStop, MainExposureFraction = (decimal)mainExposure, Burst = BurstV3 };
        if (throws)
            Assert.Throws<InvalidOperationException>(p.Validate);
        else
            p.Validate();
    }

    [Fact]
    public void PlayParameters_RefuseARallyThatFalls_ACrashThatRises_AndBurstsOutOfRange()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Rally = RallyV3 with { BtcMoveFraction = -0.13m } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Crash = CrashV3 with { BtcMoveFraction = 0.15m } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Rally = RallyV3 with { Basket = [] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Burst = BurstV3 with { OrderFraction = 0.3m } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayParameters { Burst = BurstV3 with { RegimeEmaPeriod = 1 } }.Validate());
        new PlayParameters { Burst = BurstV3, Rally = RallyV3, Crash = CrashV3 }.Validate();
    }
}
