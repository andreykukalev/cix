namespace Cix.Stocks.Host.Tests;

public class MarketSearchTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 28);
    private static readonly DateTimeOffset ThursdayRun = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    /// <summary>The whole market: what the test puts in, counting the calls (the download 100, the list 1).</summary>
    private sealed class FakeMarket : IMarketSource
    {
        public List<MarketRow> Rows { get; } = [];

        public List<ListedStock> Listed { get; } = [];

        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<MarketRow>> GetMarketDayAsync(string exchange, CancellationToken cancellationToken)
        {
            Calls += 100;
            return Fail ? throw new StockDataException("EODHD answered 403 for the US download.", 403) : Task.FromResult<IReadOnlyList<MarketRow>>(Rows);
        }

        public Task<IReadOnlyList<ListedStock>> GetListedStocksAsync(string exchange, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ListedStock>>(Listed);
        }
    }

    private static StocksConfig Config(bool enabled = true, int maxCalls = 300) => new StocksConfig
    {
        BaseCurrency = "USD",
        Capital = 10_000m,
        Benchmark = "VTI.US",
        Watchlist = [new("XYZ.US", "XYZ Corp", "USD", "tech")],
        Screener = new ScreenerSettings { Enabled = enabled, ShortlistSize = 40, MaxCallsPerDay = maxCalls },
    }.Validate();

    // FakeBars' closes rise 0.1 a day: 142.2 on Wednesday, a new 50-day high. A large liquid stock near its 250-day high.
    private static MarketRow Row(string symbol, DateOnly? day = null, decimal close = 142.2m) =>
        new(symbol, day ?? Wednesday, close, 1_000_000m, 5e9m, 135m, 120m, 143m, 1_000_000m);

    private sealed class Setup
    {
        public StocksConfig Config { get; init; } = MarketSearchTests.Config();
        public FakeBars Source { get; } = new() { LastDay = Wednesday };
        public FakeMarket Market { get; } = new();
        public InMemoryStocksStore Store { get; } = new();
        public MutableTime Time { get; } = new(ThursdayRun);

        public Setup()
        {
            Market.Rows.AddRange([Row("NEW.US"), Row("XYZ.US"), Row("DOWN.US") with { Ema50 = 110m }, Row("PINK.US")]);
            Market.Listed.AddRange([
                new("NEW.US", "New Co", "NASDAQ", "Common Stock"), new("XYZ.US", "XYZ Corp", "NYSE", "Common Stock"),
                new("DOWN.US", "Down Co", "NYSE", "Common Stock"), new("PINK.US", "Pink Co", "PINK", "Common Stock"),
            ]);
        }

        public async Task<CandidatesDocument?> RunAsync(bool advise = true)
        {
            if (advise)
                await new DailyRun(Config, Source, Store, Time).RunAsync(CancellationToken.None);
            return await new MarketSearch(Config, Market, Source, Store, Time).RunAsync(CancellationToken.None);
        }

        public SearchState? State => Store.Read<SearchState>("state/search");
    }

    [Fact]
    public async Task Run_OffSaysSoAndCallsNothing()
    {
        var s = new Setup { Config = Config(enabled: false) };

        var page = await s.RunAsync();

        Assert.Equal(SearchStatus.Off, page!.Status);
        Assert.Empty(page.Candidates);
        Assert.Equal(0, s.Market.Calls);
        Assert.NotNull(s.Store.Read<CandidatesDocument>("advice/candidates-latest"));
    }

    [Fact]
    public async Task Run_FindsABreakoutOutsideTheWatchlistAndSizesItOnTheEquity()
    {
        var s = new Setup();
        await new DailyRun(s.Config, s.Source, s.Store, s.Time).RunAsync(CancellationToken.None);
        var watchlistCalls = s.Source.Calls;
        var page = await s.RunAsync(advise: false);

        // XYZ is watched already, DOWN's 50-day EMA is under its 200-day, PINK trades over the counter: NEW alone is fetched.
        Assert.Equal((SearchStatus.Done, Wednesday, 4, 1), (page!.Status, page.AsOf, page.Listed, page.Shortlisted));
        // Stop 142.2 − 3 ATR (1) = 139.2; 1% of 10 000 over 3 + 1 is 25 shares, the 20% cap 2000 / 142.2 is 14.
        var line = Assert.Single(page.Candidates);
        Assert.Equal(new CandidateLine("NEW.US", "New Co", "NASDAQ", "breakout", 142.2m, 142.1m, 132.25m, 139.2m, 14m, line.Momentum, 5e9m), line);
        Assert.Equal(101, s.Market.Calls);
        Assert.Equal(1, s.Source.Calls - watchlistCalls);
        Assert.Equal(new SearchState(Wednesday, new DateOnly(2026, 10, 29), 102), s.State);
        Assert.NotNull(s.Store.Read<CandidatesDocument>("advice/candidates-2026-10-28"));
        Assert.NotNull(s.Store.Read<CachedBars>("bars/NEW.US"));
    }

    [Fact]
    public async Task Run_TheSameSessionIsSearchedOnce()
    {
        var s = new Setup();
        await s.RunAsync();
        s.Time.Now = s.Time.Now.AddHours(2);

        Assert.Null(await s.RunAsync());
        Assert.Equal(101, s.Market.Calls);
    }

    [Fact]
    public async Task Run_TheNextSessionReusesTheSymbolList()
    {
        var s = new Setup();
        await s.RunAsync();
        s.Source.LastDay = s.Source.LastDay.AddDays(1);
        s.Time.Now = s.Time.Now.AddDays(1);
        s.Market.Rows.Clear();
        s.Market.Rows.Add(Row("NEW.US", Wednesday.AddDays(1), 142.3m));

        var page = await s.RunAsync();

        Assert.Equal((SearchStatus.Done, Wednesday.AddDays(1)), (page!.Status, page.AsOf));
        Assert.Equal(201, s.Market.Calls); // one more download, no second symbol list
    }

    [Fact]
    public async Task Run_WithoutRoomForItsCallsNothingIsFetched()
    {
        var s = new Setup { Config = Config(maxCalls: 141) };
        await s.Store.SaveSearchStateAsync(new SearchState(null, new DateOnly(2026, 10, 29), 1), null, CancellationToken.None);

        var page = await s.RunAsync();

        Assert.Equal(SearchStatus.CallBudget, page!.Status);
        Assert.Equal(0, s.Market.Calls);
    }

    [Fact]
    public async Task Run_ARefusedDownloadSaysSoAndGivesBackTheCallsNotMade()
    {
        var s = new Setup();
        s.Market.Fail = true;

        var page = await s.RunAsync();

        Assert.Equal(SearchStatus.DataError, page!.Status);
        Assert.Null(page.AsOf);
        Assert.Equal(new SearchState(null, new DateOnly(2026, 10, 29), 101), s.State); // the list and the download, not the 40 bars
    }

    [Fact]
    public async Task Run_ADownloadFromTheSessionBeforeIsSearchedAgainNextRun()
    {
        var s = new Setup();
        s.Market.Rows.Clear();
        s.Market.Rows.Add(Row("NEW.US", Wednesday.AddDays(-1)));

        var page = await s.RunAsync();

        Assert.Equal(SearchStatus.NotReady, page!.Status);
        Assert.Null(s.State!.LastDay);
        s.Market.Rows.Clear();
        s.Market.Rows.Add(Row("NEW.US"));
        Assert.Equal(SearchStatus.Done, (await s.RunAsync(advise: false))!.Status);
    }
}
