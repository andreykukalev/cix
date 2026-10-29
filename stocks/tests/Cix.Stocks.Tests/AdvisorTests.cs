using Cix.Domain.Exchange;

namespace Cix.Stocks.Tests;

public class AdvisorTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);
    private static readonly AdvisorSettings Settings = new AdvisorSettings { BenchmarkSymbol = "CSPX.LSE" }.Validate();
    private static readonly Instrument Xyz = new("XYZ.US", "XYZ Corp", "USD", InstrumentKind.Stock);

    /// <summary>
    /// 250 daily bars ending on <paramref name="lastDay"/>: close_i = <paramref name="first"/> + <paramref name="step"/> * i,
    /// high and low half a unit either side. While |step| ≤ 0.5 every true range is 1, so ATR is exactly 1.
    /// </summary>
    internal static List<Candle> Line(decimal first, decimal step, DateOnly? lastDay = null, int count = 250)
    {
        var end = lastDay ?? Today;
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var close = first + (step * i);
                var day = end.AddDays(i - (count - 1));
                return new Candle(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), close, close + 0.5m, close - 0.5m, close, 1000m);
            })
            .ToList();
    }

    private static List<Candle> WithLastClose(List<Candle> bars, decimal close)
    {
        var last = bars[^1];
        bars[^1] = last with { Open = close, High = close + 0.5m, Low = close - 0.5m, Close = close };
        return bars;
    }

    private static readonly List<Candle> RisingBenchmark = Line(100m, 0.1m);
    private static readonly List<Candle> FallingBenchmark = Line(200m, -0.1m);

    private static AdvisorInput Input(
        IReadOnlyList<PriceHistory>? watchlist = null,
        IReadOnlyList<Candle>? benchmark = null,
        IReadOnlyList<Holding>? holdings = null,
        decimal capital = 10_000m,
        decimal cash = 10_000m,
        IReadOnlyDictionary<string, decimal>? fx = null,
        DateOnly? today = null) =>
        new(today ?? Today, "EUR", capital, cash, fx ?? new Dictionary<string, decimal> { ["USD"] = 0.9m },
            watchlist ?? [new PriceHistory(Xyz, Line(100m, 0.1m))], benchmark ?? RisingBenchmark, holdings ?? []);

    // The rising line ends at close 124.9 with ATR 1: SMA200 114.95, SMA50 122.45, prior 50-day high 124.8.

    [Fact]
    public void Evaluate_BreakoutInRiskOnMarketIsABuySizedByThePositionCap()
    {
        var advice = Advisor.Evaluate(Input(), Settings);

        Assert.Equal(MarketRegime.RiskOn, advice.Regime);
        var buy = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.Buy, buy.Kind);
        Assert.Equal(Reasons.Breakout, buy.Reason);
        Assert.Equal(124.9m, buy.ReferencePrice);
        // Stop 124.9 - 3 * 1. Risk per share (124.9 - 121.9 + 1 gap) * 0.9 = 3.6 EUR, so 100 EUR of risk allows 27.7 shares;
        // the 20% cap allows 2000 / (124.9 * 0.9) = 17.79; whole shares: 17.
        Assert.Equal(121.9m, buy.Stop);
        Assert.Equal(17m, buy.Quantity);
    }

    [Fact]
    public void Evaluate_RiskBudgetLimitsTheSizeWhenItIsTighterThanTheCap()
    {
        var advice = Advisor.Evaluate(Input(), Settings with { RiskPerTrade = 0.005m });

        // 50 EUR of risk / 3.6 EUR per share = 13.9 shares: 13.
        Assert.Equal(13m, Assert.Single(advice.Actions).Quantity);
    }

    [Fact]
    public void Evaluate_FractionalSharesAreFlooredToTheInstrumentsStep()
    {
        var fractional = Xyz with { QuantityStep = 0.001m };
        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(fractional, Line(100m, 0.1m))]), Settings);

        // 2000 / 112.41 = 17.7920...
        Assert.Equal(17.792m, Assert.Single(advice.Actions).Quantity);
    }

    [Fact]
    public void Evaluate_FreeCashLimitsTheSizeAndTooLittleCashIsANote()
    {
        var some = Advisor.Evaluate(Input(cash: 1_000m), Settings);
        Assert.Equal(8m, Assert.Single(some.Actions).Quantity); // 1000 / 112.41 = 8.9

        var none = Advisor.Evaluate(Input(cash: 50m), Settings);
        Assert.Empty(none.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.NoCash), none.Notes);
    }

    [Fact]
    public void Evaluate_NoEntriesWhileTheMarketIsRiskOff()
    {
        var advice = Advisor.Evaluate(Input(benchmark: FallingBenchmark), Settings);

        Assert.Equal(MarketRegime.RiskOff, advice.Regime);
        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("CSPX.LSE", Reasons.RegimeRiskOff), advice.Notes);
    }

    [Fact]
    public void Evaluate_NoEntriesWhenTheBenchmarkIsMissingOrStale()
    {
        foreach (var benchmark in new[] { new List<Candle>(), Line(100m, 0.1m, Today.AddDays(-6)) })
        {
            var advice = Advisor.Evaluate(Input(benchmark: benchmark), Settings);

            Assert.Equal(MarketRegime.Unknown, advice.Regime);
            Assert.Empty(advice.Actions);
            Assert.Contains(new AdvisorNote("CSPX.LSE", Reasons.RegimeUnknown), advice.Notes);
        }
    }

    [Fact]
    public void Evaluate_StaleOrFutureBarsGiveANoteAndNoAction()
    {
        foreach (var lastDay in new[] { Today.AddDays(-6), Today.AddDays(1) })
        {
            var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, Line(100m, 0.1m, lastDay))]), Settings);

            Assert.Empty(advice.Actions);
            Assert.Contains(new AdvisorNote("XYZ.US", Reasons.StaleData), advice.Notes);
        }
    }

    [Fact]
    public void Evaluate_BarsFromTheLastTradingDayBeforeAWeekendAreFresh()
    {
        // Thursday's bars on the Tuesday after Easter Monday.
        var thursday = new DateOnly(2027, 3, 25);
        var advice = Advisor.Evaluate(
            Input(watchlist: [new PriceHistory(Xyz, Line(100m, 0.1m, thursday))], benchmark: Line(100m, 0.1m, thursday), today: thursday.AddDays(5)),
            Settings);

        Assert.Equal(ActionKind.Buy, Assert.Single(advice.Actions).Kind);
    }

    [Fact]
    public void Evaluate_ShortHistoryIsNotEnough()
    {
        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, Line(100m, 0.1m, count: 150))]), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.NotEnoughData), advice.Notes);
    }

    [Fact]
    public void Evaluate_NoEntryWithoutANewClosingHigh()
    {
        // The last close equals the previous one: no breakout.
        var flatEnd = WithLastClose(Line(100m, 0.1m), 124.8m);

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, flatEnd)]), Settings);

        Assert.Empty(advice.Actions);
    }

    [Fact]
    public void Evaluate_NoEntryInADowntrend()
    {
        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, Line(200m, -0.1m))]), Settings);

        Assert.Empty(advice.Actions);
    }

    [Fact]
    public void Evaluate_ACurrencyWithoutARateIsNeverSized()
    {
        var pounds = Xyz with { Symbol = "XYZ.LSE", Currency = "GBP" };

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(pounds, Line(100m, 0.1m))]), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.LSE", Reasons.NoFxRate), advice.Notes);
    }

    [Fact]
    public void Evaluate_AFullPortfolioTakesNoNewEntries()
    {
        var holdings = Enumerable.Range(0, Settings.MaxPositions)
            .Select(i => new Holding($"H{i}.US", 1m, 10m, 9m, Today.AddDays(-30)))
            .ToList();

        var advice = Advisor.Evaluate(Input(holdings: holdings), Settings);

        Assert.DoesNotContain(advice.Actions, a => a.Kind == ActionKind.Buy);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.MaxPositions), advice.Notes);
    }

    [Fact]
    public void Evaluate_StrongestMomentumIsBoughtFirstWhenSlotsRunOut()
    {
        var slow = new Instrument("SLOW.US", "Slow", "USD", InstrumentKind.Stock);
        var fast = new Instrument("FAST.US", "Fast", "USD", InstrumentKind.Stock);
        var holdings = Enumerable.Range(0, Settings.MaxPositions - 1)
            .Select(i => new Holding($"H{i}.US", 1m, 10m, 9m, Today.AddDays(-30)))
            .ToList();
        var watchlist = new[] { new PriceHistory(slow, Line(100m, 0.1m)), new PriceHistory(fast, Line(50m, 0.1m)) };

        var advice = Advisor.Evaluate(Input(watchlist: watchlist, holdings: holdings), Settings);

        // Over 126 days both rise 12.6: FAST from 62.3 (+20%), SLOW from 112.3 (+11%).
        Assert.Equal("FAST.US", Assert.Single(advice.Actions).Symbol);
        Assert.Contains(new AdvisorNote("SLOW.US", Reasons.MaxPositions), advice.Notes);
    }

    [Fact]
    public void Evaluate_AHeldPositionWithoutAStopGetsOneUnderTheHighestClose()
    {
        var holding = new Holding("XYZ.US", 10m, 115m, null, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding]), Settings);

        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.SetStop, action.Kind);
        Assert.Equal(Reasons.NoStop, action.Reason);
        Assert.Equal(10m, action.Quantity);
        Assert.Equal(121.9m, action.Stop); // 124.9 - 3 * 1
    }

    [Fact]
    public void Evaluate_RiskOffTrailsTighter()
    {
        var holding = new Holding("XYZ.US", 10m, 115m, 121m, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding], benchmark: FallingBenchmark), Settings);

        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.RaiseStop, action.Kind);
        Assert.Equal(Reasons.RiskOffTighten, action.Reason);
        Assert.Equal(122.9m, action.Stop); // 124.9 - 2 * 1
    }

    [Fact]
    public void Evaluate_TheStopIsRaisedWhenItRisesEnough()
    {
        var holding = new Holding("XYZ.US", 10m, 115m, 121m, Today.AddDays(-49));

        var action = Assert.Single(Advisor.Evaluate(Input(holdings: [holding]), Settings).Actions);

        Assert.Equal(ActionKind.RaiseStop, action.Kind);
        Assert.Equal(Reasons.Trail, action.Reason);
        Assert.Equal(121.9m, action.Stop);
    }

    [Theory]
    [InlineData("121.8")] // 121.9 is less than 0.5% higher: not worth a change
    [InlineData("123.0")] // already above the trail: a stop is never lowered
    public void Evaluate_TheStopIsLeftAloneOtherwise(string stop)
    {
        var holding = new Holding("XYZ.US", 10m, 115m, decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture), Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding]), Settings);

        Assert.Empty(advice.Actions);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("124")]
    public void Evaluate_ACloseAtOrUnderTheStopIsASell(string stop)
    {
        // The previous close (124.8) was above the stop; the last one (123) is at or under it.
        var bars = WithLastClose(Line(100m, 0.1m), 123m);
        var holding = new Holding("XYZ.US", 10m, 115m, decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture), Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, bars)], holdings: [holding]), Settings);

        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.Sell, action.Kind);
        Assert.Equal(Reasons.BelowStop, action.Reason);
        Assert.Equal(10m, action.Quantity);
        Assert.Equal(holding.Stop, action.Stop);
    }

    [Fact]
    public void Evaluate_AStopAboveTwoClosesIsCheckedNotSold()
    {
        // Stop 380 recorded before a 4:1 split (or a typo): the broker would have sold already. Ask, don't sell.
        var holding = new Holding("XYZ.US", 10m, 115m, 380m, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding]), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.CheckHolding), advice.Notes);
    }

    [Fact]
    public void Evaluate_MissingBenchmarkDataKeepsTheNormalTrail()
    {
        var holding = new Holding("XYZ.US", 10m, 115m, 121m, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding], benchmark: []), Settings);

        Assert.Equal(MarketRegime.Unknown, advice.Regime);
        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.RaiseStop, action.Kind);
        Assert.Equal(Reasons.Trail, action.Reason);
        Assert.Equal(121.9m, action.Stop); // 3 ATR, not risk-off's 2
    }

    [Fact]
    public void Evaluate_AFlatPriceGivesNoStopAndNoSell()
    {
        // ATR 0 (a halted stock): a stop at the close would read as a breach.
        var flat = Line(100m, 0m).Select(b => b with { High = b.Close, Low = b.Close }).ToList();
        var holding = new Holding("XYZ.US", 10m, 100m, null, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, flat)], holdings: [holding]), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.NotEnoughData), advice.Notes);
    }

    [Fact]
    public void Evaluate_ADuplicateHoldingIsAdvisedOnce()
    {
        var holding = new Holding("XYZ.US", 10m, 115m, null, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(holdings: [holding, holding with { Quantity = 5m }]), Settings);

        Assert.Equal(10m, Assert.Single(advice.Actions).Quantity);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.DuplicateHolding), advice.Notes);
    }

    [Fact]
    public void Evaluate_CashBeyondTheAllocationIsNotSpent()
    {
        // 50,000 EUR in the account, a 10,000 EUR allocation of which 9,000 is held: 1,000 is left, 1000 / 112.41 = 8.9.
        var holding = new Holding("OLD.US", 100m, 90m, 80m, Today.AddDays(-100));

        var advice = Advisor.Evaluate(Input(cash: 50_000m, holdings: [holding]), Settings);

        var buy = Assert.Single(advice.Actions);
        Assert.Equal("XYZ.US", buy.Symbol);
        Assert.Equal(8m, buy.Quantity);
    }

    [Fact]
    public void Evaluate_AStopIsFlooredToACoarseTick()
    {
        var coarse = Xyz with { PriceTick = 0.5m };

        var buy = Assert.Single(Advisor.Evaluate(Input(watchlist: [new PriceHistory(coarse, Line(100m, 0.1m))]), Settings).Actions);

        // 121.9 floored to 0.5. Risk (124.9 - 121.5 + 1) * 0.9 = 3.96 allows 25.2; the cap's 17.79 decides.
        Assert.Equal(121.5m, buy.Stop);
        Assert.Equal(17m, buy.Quantity);
    }

    [Fact]
    public void Evaluate_ASmallAllocationIsTooSmallForAShare()
    {
        // Capital 100: the cap allows 20 / 112.41 = 0.18 shares, under the one whole share needed.
        var advice = Advisor.Evaluate(Input(capital: 100m, cash: 100m), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.TooSmall), advice.Notes);
    }

    [Fact]
    public void Evaluate_AStopAtOrUnderZeroIsNotAdvised()
    {
        // Closes 0.5 to 2.99 with ATR 1: the stop 2.99 - 3 is negative.
        var penny = Line(0.5m, 0.01m);

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, penny)]), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("XYZ.US", Reasons.NotEnoughData), advice.Notes);
    }

    [Fact]
    public void Evaluate_ACloseUnderTheTrendLineIsASell()
    {
        // SMA200 with the last close at 110 is 114.88.
        var bars = WithLastClose(Line(100m, 0.1m), 110m);
        var holding = new Holding("XYZ.US", 10m, 115m, 100m, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, bars)], holdings: [holding]), Settings);

        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.Sell, action.Kind);
        Assert.Equal(Reasons.TrendBroken, action.Reason);
    }

    [Fact]
    public void Evaluate_ADropThroughTheTrailingLevelIsASellEvenWithoutAStop()
    {
        // The last bar drops from 124.8 to 120: its true range is 5.3, so ATR = (19 + 5.3) / 20 = 1.215 and the trail is
        // 124.8 - 3 * 1.215 = 121.155, above the close. The close stays over the SMA200 (114.93).
        var bars = WithLastClose(Line(100m, 0.1m), 120m);
        var holding = new Holding("XYZ.US", 10m, 115m, null, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(watchlist: [new PriceHistory(Xyz, bars)], holdings: [holding]), Settings);

        var action = Assert.Single(advice.Actions);
        Assert.Equal(ActionKind.Sell, action.Kind);
        Assert.Equal(Reasons.BelowTrailingStop, action.Reason);
    }

    [Fact]
    public void Evaluate_AHeldSymbolIsNotBoughtAgainAndOneWithoutBarsGetsANote()
    {
        var holdings = new[]
        {
            new Holding("XYZ.US", 10m, 115m, 123m, Today.AddDays(-49)),
            new Holding("GONE.US", 5m, 20m, 18m, Today.AddDays(-10)),
        };

        var advice = Advisor.Evaluate(Input(holdings: holdings), Settings);

        Assert.Empty(advice.Actions);
        Assert.Contains(new AdvisorNote("GONE.US", Reasons.NoData), advice.Notes);
    }

    [Fact]
    public void Evaluate_ProtectiveActionsComeBeforeBuys()
    {
        var abc = new Instrument("ABC.US", "ABC", "USD", InstrumentKind.Stock);
        // ABC's last close is under its trend line: a sell.
        var watchlist = new[] { new PriceHistory(Xyz, Line(100m, 0.1m)), new PriceHistory(abc, WithLastClose(Line(100m, 0.1m), 110m)) };
        var holding = new Holding("ABC.US", 3m, 110m, 100m, Today.AddDays(-49));

        var advice = Advisor.Evaluate(Input(watchlist: watchlist, holdings: [holding]), Settings);

        Assert.Equal([ActionKind.Sell, ActionKind.Buy], advice.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void Evaluate_BadInputIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor.Evaluate(Input(capital: 0m), Settings));
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor.Evaluate(Input(cash: -1m), Settings));
        Assert.Throws<ArgumentException>(() => Advisor.Evaluate(Input(), Settings with { FastSmaDays = 200 }));
        Assert.Throws<ArgumentException>(() => Advisor.Evaluate(Input(), Settings with { BenchmarkSymbol = "spx" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor.Evaluate(Input(), Settings with { RiskPerTrade = 0.2m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor.Evaluate(Input(), Settings with { RiskOffTrailingStopAtr = 4m }));
    }
}
