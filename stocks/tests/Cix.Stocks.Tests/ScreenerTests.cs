using Cix.Domain.Exchange;

namespace Cix.Stocks.Tests;

public class ScreenerTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);
    private static readonly ScreenerSettings Settings = new ScreenerSettings { Enabled = true, ShortlistSize = 3 }.Validate();

    private static ListedStock Listed(string symbol, string venue = "NASDAQ", string type = "Common Stock") => new(symbol, symbol[..^3], venue, type);

    // Close 100, a large liquid stock in an uptrend 5% under its 250-day high.
    private static MarketRow Row(string symbol, decimal close = 100m, decimal? cap = 5e9m, decimal? ema50 = 95m, decimal? ema200 = 80m,
        decimal? high = 105m, decimal? avgVolume = 1_000_000m, DateOnly? day = null) =>
        new(symbol, day ?? Day, close, 1_000_000m, cap, ema50, ema200, high, avgVolume);

    [Fact]
    public void Shortlist_KeepsListedLiquidUptrendsNearTheirHighNearestFirst()
    {
        var rows = new[]
        {
            Row("AAA.US"),
            Row("BBB.US", high: 101m), // nearest its high
            Row("OTC.US"), // an over-the-counter venue
            Row("ETF.US"), // not a common stock
            Row("CHEAP.US", close: 4m, ema50: 3.8m, ema200: 3m, high: 4.1m, avgVolume: 50_000_000m),
            Row("DEAR.US", close: 250m, ema50: 240m, ema200: 200m, high: 255m),
            Row("SMALL.US", cap: 1e9m),
            Row("THIN.US", avgVolume: 100_000m), // 10 million a day
            Row("DOWN.US", ema50: 75m), // the 50-day EMA under the 200-day
            Row("UNDER.US", close: 79m, ema50: 81m, high: 85m), // the close under the 200-day EMA
            Row("FAR.US", high: 130m), // 23% under its high
            Row("OLD.US", day: Day.AddDays(-1)),
            Row("HELD.US"),
            Row("NOCAP.US", cap: null),
        };
        var universe = rows.Select(r => r.Symbol switch
        {
            "OTC.US" => Listed(r.Symbol, venue: "PINK"),
            "ETF.US" => Listed(r.Symbol, type: "ETF"),
            _ => Listed(r.Symbol),
        }).ToDictionary(l => l.Symbol);

        var shortlist = Screener.Shortlist(rows, universe, Day, 200m, new HashSet<string> { "HELD.US" }, Settings);

        Assert.Equal(["BBB.US", "AAA.US"], shortlist.Select(r => r.Symbol));
    }

    [Fact]
    public void Shortlist_StopsAtItsSizeAndSkipsWhatTheListDoesNotHave()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row($"S{i}.US", high: 100m + i)).Append(Row("GHOST.US", high: 100m)).ToList();
        var universe = rows.Where(r => r.Symbol != "GHOST.US").Select(r => Listed(r.Symbol)).ToDictionary(l => l.Symbol);

        var shortlist = Screener.Shortlist(rows, universe, Day, null, new HashSet<string>(), Settings);

        Assert.Equal(["S0.US", "S1.US", "S2.US"], shortlist.Select(r => r.Symbol));
    }

    private static List<Candle> Rising(int count, decimal step = 1m, decimal? last = null)
    {
        var start = new DateTimeOffset(Day.AddDays(-(count - 1)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(i =>
        {
            var close = i == count - 1 && last is { } l ? l : 100m + (step * i);
            return new Candle(start.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000m);
        }).ToList();
    }

    [Fact]
    public void Evaluate_ABreakoutIsSizedByTheAdvisorsRules()
    {
        // 250 rising closes: the last, 349, beats the prior high 348. Each day's true range is 1.5 (the high over the previous
        // close), so the stop is 349 − 3 × 1.5 and the risk 4.5 + 1.5 a share.
        var candidate = Screener.Evaluate("XYZ.US", "XYZ", "NASDAQ", 5e9m, Rising(250), Day, 10_000m, new AdvisorSettings(), Settings);

        Assert.NotNull(candidate);
        Assert.Equal((EntryState.Breakout, 349m, 348m, 344.5m), (candidate.State, candidate.Close, candidate.PriorHigh, candidate.Stop));
        // 1% of 10 000 over 6 is 16 shares; the 20% cap, 2000 / 349, is 5.
        Assert.Equal(5m, candidate.Shares);
        Assert.Equal(Day, candidate.Day);
    }

    [Fact]
    public void Evaluate_ListsAnUptrendNearItsHighAndDropsTheRest()
    {
        var advisor = new AdvisorSettings();
        // The last close dips to 340: 348 is 2.4% above it, within 3%.
        Assert.Equal(EntryState.Uptrend, Screener.Evaluate("XYZ.US", null, null, null, Rising(250, last: 340m), Day, 10_000m, advisor, Settings)!.State);
        // A dip to 330: 5.5% under the high, not listed.
        Assert.Null(Screener.Evaluate("XYZ.US", null, null, null, Rising(250, last: 330m), Day, 10_000m, advisor, Settings));
        // Falling, too short, or old bars: not listed.
        Assert.Null(Screener.Evaluate("XYZ.US", null, null, null, Rising(250, step: -0.1m), Day, 10_000m, advisor, Settings));
        Assert.Null(Screener.Evaluate("XYZ.US", null, null, null, Rising(150), Day, 10_000m, advisor, Settings));
        Assert.Null(Screener.Evaluate("XYZ.US", null, null, null, Rising(250), Day.AddDays(30), 10_000m, advisor, Settings));
    }

    [Fact]
    public void Rank_PutsEntrySignalsFirstThenMomentum()
    {
        Candidate C(string symbol, EntryState state, decimal momentum) => new(symbol, null, null, Day, state, 10m, 9m, 8m, 1m, 7m, 1m, momentum, null);

        var ranked = Screener.Rank([C("A.US", EntryState.Uptrend, 0.9m), C("B.US", EntryState.Breakout, 0.1m), C("C.US", EntryState.Breakout, 0.5m)], Settings);

        Assert.Equal(["C.US", "B.US", "A.US"], ranked.Select(c => c.Symbol));
    }

    [Fact]
    public void Settings_TheCallBudgetMustHoldTheDownloadAndTheShortlist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenerSettings { ShortlistSize = 40, MaxCallsPerDay = 140 }.Validate());
        Assert.Equal(141, new ScreenerSettings { ShortlistSize = 40, MaxCallsPerDay = 141 }.Validate().MaxCallsPerDay);
        Assert.Throws<ArgumentException>(() => new ScreenerSettings { Exchange = "us" }.Validate());
    }
}
