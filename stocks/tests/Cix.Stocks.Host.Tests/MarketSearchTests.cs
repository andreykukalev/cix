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

        public IStocksStore? SearchStore { get; set; }

        public DateTimeOffset? Deadline { get; set; }

        public async Task<CandidatesDocument?> RunAsync(bool advise = true)
        {
            if (advise)
                await new DailyRun(Config, Source, Store, Time).RunAsync(CancellationToken.None);
            return await new MarketSearch(Config, Market, Source, SearchStore ?? Store, Time, Deadline).RunAsync(CancellationToken.None);
        }

        public SearchState? State => Store.Read<SearchState>("state/search");
    }

    [Fact]
    public async Task Run_WithTheOwnersPortfolioSizesOnItsCapitalAndLeavesOutWhatTheOwnerHolds()
    {
        var s = new Setup();
        s.Market.Rows.Add(Row("MINE.US"));
        s.Market.Listed.Add(new("MINE.US", "Mine Co", "NYSE", "Common Stock"));
        s.Store.Submit(new PortfolioSubmission(1, ThursdayRun.AddHours(-8), 5_000m, [new("MINE.US", null, 3m, 100m, null, null)]));

        var page = await s.RunAsync();

        // MINE is the owner's (advised in their section), so NEW alone: 1% of 5000 over 3 + 1 is 12 shares, the cap 1000 / 142.2 is 7.
        var line = Assert.Single(page!.Candidates);
        Assert.Equal(("NEW.US", 7m), (line.Symbol, line.Shares));
        Assert.Equal(5_000m, page.Capital);
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

        // PINK trades over the counter (not kept in the list), XYZ is watched already, DOWN's 50-day EMA is under its 200-day:
        // NEW alone is fetched.
        Assert.Equal((SearchStatus.Done, Wednesday, 3, 1), (page!.Status, page.AsOf, page.Listed, page.Shortlisted));
        Assert.DoesNotContain(s.Store.Read<StoredUniverse>("state/universe")!.Stocks, l => l.Symbol == "PINK.US");
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

    [Fact]
    public async Task Run_ADownloadForALaterSessionThanTheAdviceWaitsForTheAdvice()
    {
        var s = new Setup();
        s.Market.Rows.Clear();
        s.Market.Rows.Add(Row("NEW.US", Wednesday.AddDays(1)));

        var page = await s.RunAsync();

        Assert.Equal(SearchStatus.AdviceBehind, page!.Status);
        Assert.Null(s.State!.LastDay);
    }

    [Fact]
    public async Task Run_CachedBarsSpendNoCallAndAFailedFetchIsCountedAndSkipped()
    {
        var s = new Setup();
        s.Market.Rows.Add(Row("FAIL.US"));
        s.Market.Listed.Add(new("FAIL.US", "Fail Co", "NYSE", "Common Stock"));
        s.Source.Failing.Add("FAIL.US");
        await new DailyRun(s.Config, s.Source, s.Store, s.Time).RunAsync(CancellationToken.None);
        await s.Store.SaveBarsAsync(new CachedBars("NEW.US", ThursdayRun, (await s.Source.GetDailyBarsAsync("NEW.US", Wednesday.AddDays(-400), Wednesday, CancellationToken.None)).Select(StoredBar.From).ToList()), CancellationToken.None);
        var before = s.Source.Calls;

        var page = await s.RunAsync(advise: false);

        Assert.Equal(["NEW.US"], page!.Candidates.Select(c => c.Symbol));
        Assert.Equal(1, s.Source.Calls - before); // FAIL's failed fetch; NEW from the cache
        Assert.Equal(102, s.State!.Calls); // the list (1), the download (100) and FAIL's failed fetch
    }

    [Fact]
    public async Task Run_PastTheDeadlineNoMoreBarsAreFetched()
    {
        var s = new Setup { Deadline = ThursdayRun };

        var page = await s.RunAsync();

        Assert.Equal((SearchStatus.Done, 1, 0), (page!.Status, page.Shortlisted, page.Candidates.Count));
    }

    [Fact]
    public async Task Run_APageLeftBehindByAFailedRunIsCopiedFromTheDatedOne()
    {
        var s = new Setup();
        await s.RunAsync();
        // A run that saved its state and the dated page, then failed before the latest one: an older page is shown.
        var latest = s.Store.Read<CandidatesDocument>("advice/candidates-latest")!;
        await s.Store.SaveLatestCandidatesAsync(latest with { AsOf = Wednesday.AddDays(-1) }, CancellationToken.None);

        Assert.Null(await s.RunAsync(advise: false));

        Assert.Equal(Wednesday, s.Store.Read<CandidatesDocument>("advice/candidates-latest")!.AsOf);
        Assert.Equal(101, s.Market.Calls);
    }

    [Fact]
    public async Task Run_TwoRunsReservingAtOnceConflict()
    {
        var s = new Setup();
        await new DailyRun(s.Config, s.Source, s.Store, s.Time).RunAsync(CancellationToken.None);
        s.SearchStore = new RacingSearchStore(s.Store);

        await Assert.ThrowsAsync<StoreConflictException>(() => s.RunAsync(advise: false));
        Assert.Equal(0, s.Market.Calls);
    }

    /// <summary>Another run reserves the search's calls between this run's read and its reservation.</summary>
    private sealed class RacingSearchStore(InMemoryStocksStore inner) : IStocksStore
    {
        public async Task<Versioned<SearchState>?> GetSearchStateAsync(CancellationToken cancellationToken)
        {
            var state = await inner.GetSearchStateAsync(cancellationToken);
            await inner.SaveSearchStateAsync(new SearchState(null, new DateOnly(2026, 10, 29), 141), state?.ETag, cancellationToken);
            return state;
        }

        public Task<string> SaveSearchStateAsync(SearchState state, string? etag, CancellationToken cancellationToken) => inner.SaveSearchStateAsync(state, etag, cancellationToken);
        public Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) => inner.GetBarsAsync(symbol, cancellationToken);
        public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken) => inner.SaveBarsAsync(bars, cancellationToken);
        public Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken) => inner.GetStateAsync(cancellationToken);
        public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) => inner.SaveStateAsync(state, etag, cancellationToken);
        public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) => inner.AddTradeAsync(id, trade, cancellationToken);
        public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) => inner.AddAdviceAsync(id, advice, cancellationToken);
        public Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) => inner.GetAdviceAsync(id, cancellationToken);
        public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) => inner.SaveLatestAdviceAsync(advice, cancellationToken);
        public Task<StoredUniverse?> GetUniverseAsync(CancellationToken cancellationToken) => inner.GetUniverseAsync(cancellationToken);
        public Task SaveUniverseAsync(StoredUniverse universe, CancellationToken cancellationToken) => inner.SaveUniverseAsync(universe, cancellationToken);
        public Task AddCandidatesAsync(string id, CandidatesDocument candidates, CancellationToken cancellationToken) => inner.AddCandidatesAsync(id, candidates, cancellationToken);
        public Task<CandidatesDocument?> GetCandidatesAsync(string id, CancellationToken cancellationToken) => inner.GetCandidatesAsync(id, cancellationToken);
        public Task SaveLatestCandidatesAsync(CandidatesDocument candidates, CancellationToken cancellationToken) => inner.SaveLatestCandidatesAsync(candidates, cancellationToken);

        public Task<Versioned<OwnerState>?> GetOwnerStateAsync(CancellationToken cancellationToken) => inner.GetOwnerStateAsync(cancellationToken);

        public Task<string> SaveOwnerStateAsync(OwnerState state, string? etag, CancellationToken cancellationToken) => inner.SaveOwnerStateAsync(state, etag, cancellationToken);

        public Task<SubmissionRead?> GetPortfolioSubmissionAsync(int sequence, CancellationToken cancellationToken) => inner.GetPortfolioSubmissionAsync(sequence, cancellationToken);
    }

    [Fact]
    public void Config_TheSearchSizesInUsdOnly()
    {
        var eur = () => new StocksConfig
        {
            BaseCurrency = "EUR",
            Capital = 10_000m,
            Benchmark = "VTI.US",
            Fx = new Dictionary<string, decimal> { ["USD"] = 0.9m },
            Watchlist = [new("XYZ.US", "XYZ Corp", "USD")],
            Screener = new ScreenerSettings { Enabled = true },
        }.Validate();

        Assert.Throws<ArgumentException>(eur);
    }
}
