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
        for (var day = firstDay < Start ? Start : firstDay; day <= lastDay && day <= LastDay; day = day.AddDays(1))
        {
            var close = Close(symbol, day) * factor;
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
        Assert.Equal(new TradeLine(new DateOnly(2026, 10, 29), "XYZ.US", "buy", 14m, price, fees, null, Reasons.Breakout), trade);
        Assert.NotNull(s.Store.Read<TradeLine>("trades/2026-10-29-XYZ.US-buy"));
        var holding = Assert.Single(s.Latest.Holdings);
        Assert.Equal(new HoldingLine("XYZ.US", "XYZ Corp", "USD", 14m, price, 142.3m, 139.2m, new DateOnly(2026, 10, 29)), holding);
        Assert.Equal(10_000m - (14m * price) - fees, s.Latest.Cash);
        Assert.Equal(s.Latest.Cash + (14m * 142.3m), s.Latest.Equity);
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
        Assert.Equal((28m, 69.6m, 142.3m * 1.001m / 2), (holding.Quantity, holding.Stop!.Value, holding.AverageCost));
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

        // 1% of Thursday's close on 14 shares.
        Assert.Equal(cash + (14m * 142.3m * 0.01m), s.State.Cash);
        Assert.Equal(14m, Assert.Single(s.Latest.Holdings).Quantity);
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
    public void AdjustmentFactor_ComparesTheLatestCommonClose()
    {
        var day = new DateOnly(2026, 10, 28);
        StoredBar Stored(DateOnly d, decimal close) => new(d, close, close, close, close, 0m);
        Candle Fresh(DateOnly d, decimal close) => new(new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), close, close, close, close, 0m);

        Assert.Equal((0.5m, 100m), DailyRun.AdjustmentFactor([Stored(day.AddDays(-1), 90m), Stored(day, 100m)], [Fresh(day, 50m), Fresh(day.AddDays(1), 51m)]));
        Assert.Null(DailyRun.AdjustmentFactor([Stored(day, 100m)], [Fresh(day, 100.01m)]));
        Assert.Null(DailyRun.AdjustmentFactor([Stored(day, 100m)], [Fresh(day.AddDays(1), 50m)]));
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

        public Task SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) => inner.SaveStateAsync(state, etag, cancellationToken);

        public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) => inner.AddTradeAsync(id, trade, cancellationToken);

        public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) => inner.AddAdviceAsync(id, advice, cancellationToken);

        public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) => inner.SaveLatestAdviceAsync(advice, cancellationToken);
    }
}
