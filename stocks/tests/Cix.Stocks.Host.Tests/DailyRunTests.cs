using Cix.Domain.Exchange;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Host.Tests;

public sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Bars on every calendar day from <see cref="Start"/>: close = 100 + 0.1 × days since the start (falling for ABC), the open
/// at the close and the high and low half a unit either side, so ATR is 1. Only days up to <see cref="LastDay"/> exist yet.
/// </summary>
public sealed class FakeBars : IDailyBarSource
{
    public static readonly DateOnly Start = new(2025, 9, 1);

    public DateOnly LastDay { get; set; }

    public int Calls { get; private set; }

    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>A different close (and open) on one day.</summary>
    public Dictionary<(string Symbol, DateOnly Day), decimal> Override { get; } = [];

    /// <summary>Symbols EODHD answers with no bars at all.</summary>
    public HashSet<string> Empty { get; } = new(StringComparer.Ordinal);

    /// <summary>The provider's adjustment factor per symbol, applied to its whole history (0.5: a two-for-one split).</summary>
    public Dictionary<string, decimal> Factor { get; } = new(StringComparer.Ordinal);

    public static decimal Close(string symbol, DateOnly day) =>
        symbol == "ABC.US" ? 200m - (0.1m * (day.DayNumber - Start.DayNumber)) : 100m + (0.1m * (day.DayNumber - Start.DayNumber));

    public Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string symbol, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failing.Contains(symbol))
            throw new StockDataException($"EODHD answered 402 for {symbol}.", 402);
        var factor = Factor.GetValueOrDefault(symbol, 1m);
        var bars = new List<Candle>();
        if (Empty.Contains(symbol))
            return Task.FromResult<IReadOnlyList<Candle>>(bars);
        for (var day = firstDay < Start ? Start : firstDay; day <= lastDay && day <= LastDay; day = day.AddDays(1))
        {
            var close = (Override.TryGetValue((symbol, day), out var o) ? o : Close(symbol, day)) * factor;
            bars.Add(new Candle(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), close, close + 0.5m, close - 0.5m, close, 1000m));
        }

        return Task.FromResult<IReadOnlyList<Candle>>(bars);
    }
}

public class DailyRunTests
{
    // Wednesday 28 October 2026's close, run at 02:00 UTC on Thursday.
    private static readonly DateOnly Wednesday = new(2026, 10, 28);
    private static readonly DateTimeOffset ThursdayRun = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    private static StocksConfig Config(int maxCallsPerDay = 18) => new StocksConfig
    {
        BaseCurrency = "USD",
        Capital = 10_000m,
        Benchmark = "VTI.US",
        MaxCallsPerDay = maxCallsPerDay,
        Watchlist = [new("XYZ.US", "XYZ Corp", "USD", "tech"), new("ABC.US", "ABC Corp", "USD")],
    }.Validate();

    private sealed class Setup
    {
        public StocksConfig Config { get; init; } = DailyRunTests.Config();
        public FakeBars Source { get; } = new() { LastDay = Wednesday };
        public InMemoryStocksStore Store { get; } = new();
        public MutableTime Time { get; } = new(ThursdayRun);

        public Task<RunReport> RunAsync() => new DailyRun(Config, Source, Store, Time).RunAsync(CancellationToken.None);

        public AdviceDocument Latest => Store.Read<AdviceDocument>("advice/advice-latest")!;

        public PaperState State => Store.Read<PaperState>("state/paper")!;

        /// <summary>The next session's bars are out, and the run comes the night after.</summary>
        public void NextDay()
        {
            Source.LastDay = Source.LastDay.AddDays(1);
            Time.Now = Time.Now.AddDays(1);
        }
    }

    [Fact]
    public async Task Run_TheFirstCloseIsAdvisedAndTheBuyWaitsForTheOpen()
    {
        var s = new Setup();

        var report = await s.RunAsync();

        Assert.Equal(new RunReport(RunOutcome.Advised, Wednesday, 3, 0, 1, 0), report);
        // XYZ closed at 142.2 (422 days after the start): stop 139.2, risk 1% of 10000 over 3 + 1 ATR is 25 shares, the 20%
        // cap 2000 / 142.2 is 14.
        var buy = Assert.Single(s.Latest.Actions);
        Assert.Equal(new ActionLine("buy", "XYZ.US", "XYZ Corp", "USD", 14m, 142.2m, 139.2m, Reasons.Breakout), buy);
        Assert.Equal(("risk-on", 10_000m, 10_000m, 10_000m), (s.Latest.Regime, s.Latest.Capital, s.Latest.Cash, s.Latest.Equity));
        Assert.Empty(s.Latest.Holdings);
        Assert.Equal(Wednesday, s.State.LastAdvisedDay);
        Assert.Equal(3, s.State.Calls);
        Assert.NotNull(s.Store.Read<AdviceDocument>("advice/advice-2026-10-28"));
    }

    [Fact]
    public async Task Run_TheWatchlistShowsEachStocksTrendAndTheHighItMustBeat()
    {
        var s = new Setup();

        await s.RunAsync();

        // XYZ rises 0.1 a day: the close 142.2 beats the prior 50-day high 142.1 (the day before), above its 200-day average
        // (days 223–422: 100 + 0.1 × 322.5). ABC falls 0.1 a day: its close 157.8 is under the average 200 − 32.25, and its
        // prior high is the oldest of the 50 closes, 200 − 37.2.
        Assert.Equal(
            [
                new WatchLine("XYZ.US", "XYZ Corp", "USD", "tech", 142.2m, Wednesday, 142.1m, 132.25m, WatchStatus.Breakout),
                new WatchLine("ABC.US", "ABC Corp", "USD", null, 157.8m, Wednesday, 162.8m, 167.75m, WatchStatus.BelowTrend),
            ],
            s.Latest.Watchlist!);

        s.NextDay();
        await s.RunAsync();

        Assert.Equal(WatchStatus.Held, s.Latest.Watchlist!.Single(w => w.Symbol == "XYZ.US").Status);
    }

    [Fact]
    public void WatchStatus_GivesTheAdvisorsEntryVerdictForEachWatchedStock()
    {
        var item = new WatchItem("XYZ.US", "XYZ Corp", "USD");
        var settings = new AdvisorSettings();
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Candle Bar(int i, decimal close) => new(start.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1m);
        // 250 rising closes, then a dip: still above the 200-day average, the 50-day above it, under the prior high 349.
        var bars = Enumerable.Range(0, 250).Select(i => Bar(i, 100m + i)).Append(Bar(250, 340m)).ToList();
        var today = DateOnly.FromDateTime(start.AddDays(250).UtcDateTime);
        string Status(IReadOnlyList<Candle> b, bool held = false, DateOnly? on = null, decimal? fx = 1m, AdvisorSettings? with = null) =>
            WatchStatus.For(item, b, held, on ?? today, fx, with ?? settings).Status;

        var uptrend = WatchStatus.For(item, bars, held: false, today, 1m, settings);
        Assert.Equal((WatchStatus.Uptrend, 349m, 340m, today), (uptrend.Status, uptrend.BreakoutHigh!.Value, uptrend.LastClose!.Value, uptrend.LastDay!.Value));
        Assert.Equal(WatchStatus.Breakout, Status([.. bars.SkipLast(1), Bar(250, 360m)]));
        // A rally after a long fall: the close is over the 200-day average but the 50-day is still under it.
        var turning = Enumerable.Range(0, 240).Select(i => Bar(i, 400m - i)).Concat(Enumerable.Range(240, 11).Select(i => Bar(i, 160m + (40m * (i - 239))))).ToList();
        Assert.Equal(WatchStatus.BelowTrend, Status(turning));
        // The advisor's other checks, in its order: old bars, the price cap, short history; held comes first.
        Assert.Equal(WatchStatus.StaleData, Status(bars, on: today.AddDays(settings.MaxBarAgeDays + 1)));
        Assert.Equal(WatchStatus.TooExpensive, Status(bars, with: settings with { MaxSharePrice = 300m }));
        Assert.Equal(WatchStatus.Uptrend, Status(bars, fx: null, with: settings with { MaxSharePrice = 300m })); // no rate: the cap can't be judged
        Assert.Equal(WatchStatus.NotEnoughData, Status(bars.Take(150).ToList(), on: DateOnly.FromDateTime(start.AddDays(149).UtcDateTime)));
        Assert.Equal(WatchStatus.NoData, Status([]));
        Assert.Equal(WatchStatus.Held, Status([], held: true));
    }

    [Fact]
    public async Task Run_ARepeatedRunTheSameNightSpendsNoCallsAndChangesNothing()
    {
        var s = new Setup();
        await s.RunAsync();
        var first = s.Store.Documents["advice/advice-latest"];
        s.Time.Now = s.Time.Now.AddHours(3);

        var report = await s.RunAsync();

        Assert.Equal(new RunReport(RunOutcome.AlreadyAdvised, Wednesday, 0, 0, 0, 0), report);
        Assert.Equal(3, s.Source.Calls);
        Assert.Equal(first, s.Store.Documents["advice/advice-latest"]);
    }

    [Fact]
    public async Task Run_TheNextNightCarriesTheBuyOutAtTheOpenAndKeepsTheStop()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();

        var report = await s.RunAsync();

        Assert.Equal((RunOutcome.Advised, 1), (report.Outcome, report.Trades));
        // Thursday's open is 142.3, plus 0.1% slippage; commission 0.25% of the value (above the 1 USD minimum), no FX fee.
        var price = 142.3m * 1.001m;
        var fees = 14m * price * 0.0025m;
        var trade = Assert.Single(s.Latest.Trades);
        // The page shows money to the cent; the saved portfolio keeps every digit.
        Assert.Equal(new TradeLine(new DateOnly(2026, 10, 29), "XYZ.US", "buy", 14m, price, 4.99m, null, Reasons.Breakout), trade);
        Assert.NotNull(s.Store.Read<TradeLine>("trades/2026-10-29-XYZ.US-buy"));
        var holding = Assert.Single(s.Latest.Holdings);
        Assert.Equal(new HoldingLine("XYZ.US", "XYZ Corp", "USD", 14m, price, 142.3m, 139.2m, new DateOnly(2026, 10, 29)), holding);
        Assert.Equal(10_000m - (14m * price) - fees, s.State.Cash);
        Assert.Equal(Math.Round(s.State.Cash, 2), s.Latest.Cash);
        Assert.Equal(Math.Round(s.State.Cash + (14m * 142.3m), 2), s.Latest.Equity);
        Assert.Equal(6, s.Source.Calls);
    }

    [Fact]
    public async Task Run_OverTheDaysBudgetASymbolIsNotedAndTheCountHoldsAcrossRuns()
    {
        var s = new Setup { Config = Config(maxCallsPerDay: 2) };

        await s.RunAsync();

        // The benchmark and XYZ are fetched; ABC is over the budget and has no bars yet.
        Assert.Contains(new NoteLine("ABC.US", HostReasons.CallBudget), s.Latest.Notes);
        Assert.Contains(new NoteLine("ABC.US", Reasons.NoData), s.Latest.Notes);
        s.Time.Now = s.Time.Now.AddHours(1);
        await s.RunAsync();
        Assert.Equal(2, s.Source.Calls);
        Assert.Equal(2, s.State.Calls);

        // A new UTC day has a new budget.
        s.NextDay();
        await s.RunAsync();
        Assert.Equal(4, s.Source.Calls);
    }

    [Fact]
    public async Task Run_AFailedFetchKeepsTheOlderBarsWithANote()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        s.Source.Failing.Add("XYZ.US");

        await s.RunAsync();

        Assert.Contains(new NoteLine("XYZ.US", HostReasons.DataError), s.Latest.Notes);
        // Without Thursday's bar for XYZ, its buy waits; the newer advice decides again.
        Assert.Empty(s.Latest.Trades);
        // The failed call counts against the new day's budget too.
        Assert.Equal((6, 3), (s.Source.Calls, s.State.Calls));
    }

    [Fact]
    public async Task Run_ASplitMovesTheHeldPositionIntoTheNewTermsWithoutAStopOut()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        await s.RunAsync(); // bought 14 XYZ at Thursday's open, stop 139.2
        s.NextDay();
        s.Source.Factor["XYZ.US"] = 0.5m; // two for one: every price halves

        await s.RunAsync();

        Assert.DoesNotContain(s.Latest.Trades, t => t.Kind == "stop-out");
        var holding = Assert.Single(s.Latest.Holdings);
        Assert.Equal((28m, 69.6m, Math.Round(142.3m * 1.001m / 2, 4)), (holding.Quantity, holding.Stop!.Value, holding.AverageCost));
        Assert.Equal(142.3m * 1.001m / 2, Assert.Single(s.State.Positions).Holding.AverageCost);
    }

    [Fact]
    public async Task Run_ADividendAdjustmentIsPaidInCash()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        await s.RunAsync();
        var cash = s.State.Cash;
        s.NextDay();
        s.Source.Factor["XYZ.US"] = 0.99m;

        await s.RunAsync();

        // 1% of the close on 14 shares, from Wednesday's (Thursday's, the newest cached, may have been revised).
        Assert.Equal(cash + (14m * 142.2m * 0.01m), s.State.Cash);
        Assert.Equal(14m, Assert.Single(s.Latest.Holdings).Quantity);
        Assert.Contains(new NoteLine("XYZ.US", HostReasons.Adjusted), s.Latest.Notes);
    }

    [Fact]
    public async Task Run_AnEmptyAnswerKeepsTheOlderBars()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        s.Source.Empty.Add("XYZ.US");

        await s.RunAsync();

        Assert.Contains(new NoteLine("XYZ.US", HostReasons.DataError), s.Latest.Notes);
        Assert.Equal(365, s.Store.Read<CachedBars>("bars/XYZ.US")!.Bars.Count);
    }

    [Fact]
    public async Task Run_APageLeftBehindByAFailedRunIsRepublishedTheSameNight()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        var failing = new FaultyStore(s.Store);
        await Assert.ThrowsAsync<IOException>(() => new DailyRun(s.Config, s.Source, failing, s.Time).RunAsync(CancellationToken.None));
        Assert.Equal(Wednesday, s.Latest.AsOf);

        s.Time.Now = s.Time.Now.AddHours(1);
        var report = await s.RunAsync();

        Assert.Equal(RunOutcome.AlreadyAdvised, report.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 29), s.Latest.AsOf);
        Assert.Single(s.Latest.Trades);
    }

    [Fact]
    public async Task Run_ASplitIsStillRecognisedWhenTheRunThatFetchedItFailed()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        await s.RunAsync(); // 14 XYZ, stop 139.2
        s.NextDay();
        s.Source.Factor["XYZ.US"] = 0.5m;
        // The split's bars are cached, then the run fails before the portfolio is saved.
        await Assert.ThrowsAsync<IOException>(() => new DailyRun(s.Config, s.Source, new FaultyStore(s.Store, failAdvice: true), s.Time).RunAsync(CancellationToken.None));
        s.Time.Now = s.Time.Now.AddHours(1);

        await s.RunAsync();

        Assert.DoesNotContain(s.Latest.Trades, t => t.Kind == "stop-out");
        Assert.Equal((28m, 69.6m), (Assert.Single(s.Latest.Holdings).Quantity, s.Latest.Holdings[0].Stop));
    }

    [Fact]
    public async Task Run_AHeldSymbolsLateSessionStillHasItsStopChecked()
    {
        var s = new Setup();
        await s.RunAsync();
        s.NextDay();
        await s.RunAsync(); // 14 XYZ on Thursday, stop 139.2
        s.NextDay();
        var friday = new DateOnly(2026, 10, 30);
        s.Source.Override[("XYZ.US", friday)] = 130m; // Friday trades under the stop...
        s.Source.Failing.Add("XYZ.US"); // ...but its bars don't come that night
        await s.RunAsync();
        Assert.Empty(s.Latest.Trades);
        s.NextDay();
        s.Source.Failing.Clear();

        await s.RunAsync();

        // Friday is caught up: the stop sold at Friday's open, 130, under the stop, less 0.1% slippage.
        var stopOut = Assert.Single(s.Latest.Trades);
        Assert.Equal((friday, "stop-out", 14m, 130m * 0.999m), (stopOut.Day, stopOut.Kind, stopOut.Quantity, stopOut.Price));
        Assert.Empty(s.Latest.Holdings);
    }

    [Fact]
    public async Task Run_AnOverlappingRunFailsBeforeSpendingCalls()
    {
        var s = new Setup();
        var racing = new RacingStore(s.Store);

        await Assert.ThrowsAsync<StoreConflictException>(() => new DailyRun(s.Config, s.Source, racing, s.Time).RunAsync(CancellationToken.None));

        Assert.Equal(0, s.Source.Calls);
    }

    [Fact]
    public async Task Run_AnotherRunSavingFirstDropsThisOne()
    {
        var inner = new InMemoryStocksStore();
        var store = new RacingStore(inner);
        var run = new DailyRun(Config(), new FakeBars { LastDay = Wednesday }, store, new MutableTime(ThursdayRun));

        await Assert.ThrowsAsync<StoreConflictException>(() => run.RunAsync(CancellationToken.None));
        Assert.False(inner.Documents.ContainsKey("advice/advice-latest"));
    }

    [Fact]
    public async Task Run_WithoutAnyBarsNothingIsAdvised()
    {
        var s = new Setup();
        s.Source.LastDay = FakeBars.Start.AddDays(-1);

        var report = await s.RunAsync();

        Assert.Equal(RunOutcome.NoData, report.Outcome);
        Assert.False(s.Store.Documents.ContainsKey("advice/advice-latest"));
        Assert.Equal(3, s.State.Calls);
    }

    [Fact]
    public void AdjustmentFactor_NeedsTheSameRatioOnTheSavedDays()
    {
        var day = new DateOnly(2026, 10, 28);
        Candle Bar(DateOnly d, decimal close) => new(new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), close, close, close, close, 0m);
        StoredClose[] saved = [new(day.AddDays(-2), 80m), new(day.AddDays(-1), 90m)];

        // A split halves every bar: the factor, with the latest saved close.
        Assert.Equal((0.5m, 90m), DailyRun.AdjustmentFactor(saved, [Bar(day.AddDays(-2), 40m), Bar(day.AddDays(-1), 45m), Bar(day, 50m)]));
        // Days that disagree are a revised bar, not an adjustment; nor is rounding; one day in common is too little.
        Assert.Null(DailyRun.AdjustmentFactor(saved, [Bar(day.AddDays(-2), 40m), Bar(day.AddDays(-1), 90m)]));
        Assert.Null(DailyRun.AdjustmentFactor(saved, [Bar(day.AddDays(-2), 80.01m), Bar(day.AddDays(-1), 90.01m)]));
        Assert.Null(DailyRun.AdjustmentFactor(saved, [Bar(day.AddDays(-1), 45m), Bar(day, 50m)]));
    }

    /// <summary>The run stops at one write: the dated advice (before the portfolio is saved) or the page (after).</summary>
    private sealed class FaultyStore(InMemoryStocksStore inner, bool failAdvice = false) : IStocksStore
    {
        public Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) => inner.GetBarsAsync(symbol, cancellationToken);

        public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken) => inner.SaveBarsAsync(bars, cancellationToken);

        public Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken) => inner.GetStateAsync(cancellationToken);

        public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) => inner.SaveStateAsync(state, etag, cancellationToken);

        public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) => inner.AddTradeAsync(id, trade, cancellationToken);

        public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) =>
            failAdvice ? throw new IOException("connection lost") : inner.AddAdviceAsync(id, advice, cancellationToken);

        public Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) => inner.GetAdviceAsync(id, cancellationToken);

        public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) =>
            failAdvice ? inner.SaveLatestAdviceAsync(advice, cancellationToken) : throw new IOException("connection lost");
    }

    /// <summary>Another run saves the portfolio between this run's read and its write.</summary>
    private sealed class RacingStore(InMemoryStocksStore inner) : IStocksStore
    {
        public Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) => inner.GetBarsAsync(symbol, cancellationToken);

        public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken) => inner.SaveBarsAsync(bars, cancellationToken);

        public async Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken)
        {
            var state = await inner.GetStateAsync(cancellationToken);
            await inner.SaveStateAsync(PaperState.Start(Config()), state?.ETag, cancellationToken);
            return state;
        }

        public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) => inner.SaveStateAsync(state, etag, cancellationToken);

        public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) => inner.AddTradeAsync(id, trade, cancellationToken);

        public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) => inner.AddAdviceAsync(id, advice, cancellationToken);

        public Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) => inner.GetAdviceAsync(id, cancellationToken);

        public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) => inner.SaveLatestAdviceAsync(advice, cancellationToken);
    }
}
