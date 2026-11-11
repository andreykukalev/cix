using System.Text.Json;

namespace Cix.Stocks.Host.Tests;

public class AnalystTests
{
    // Friday 30 October 2026's close, analysed at 02:30 UTC on Saturday.
    private static readonly DateOnly Friday = new(2026, 10, 30);
    private static readonly DateTimeOffset Night = new(2026, 10, 31, 2, 30, 0, TimeSpan.Zero);

    private sealed class FakeNews : INewsSource
    {
        public List<(string Symbol, DateOnly From, int Limit)> Asked { get; } = [];
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, DateOnly from, int limit, CancellationToken cancellationToken)
        {
            Asked.Add((symbol, from, limit));
            if (Failing.Contains(symbol))
                throw new StockDataException("EODHD refused the request.", 500);
            return Task.FromResult<IReadOnlyList<NewsItem>>([new(Night.AddDays(-1), $"{symbol} news", null, "reuters.com", 0.2m)]);
        }
    }

    private static HoldingLine Holding(string symbol, decimal quantity = 10m, decimal stop = 90m) =>
        new(symbol, $"{symbol} Corp", "USD", quantity, 100m, 110m, stop, new DateOnly(2026, 9, 1));

    private static ActionLine Action(string kind, string symbol, decimal quantity = 5m) =>
        new(kind, symbol, $"{symbol} Corp", "USD", quantity, 50m, 45m, kind == "buy" ? "breakout" : "stop-hit");

    private static AdviceDocument Advice(DateOnly asOf, OwnerSection? owner = null) => new(
        asOf, Night.AddMinutes(-30), "risk-on", "VTI.US", "USD", 10_000m, 4_000m, 10_000m,
        [Action("buy", "PAPERBUY.US")], [Holding("PAPER.US")], [], [], [], Owner: owner);

    private static OwnerSection Owner() => new(
        20_000m, 2_000m, 18_000m, 19_000m, 3, Night.AddDays(-2),
        [Action("sell", "OWN.US", 10m), Action("buy", "NEW.US")], [Holding("OWN.US"), Holding("KEEP.US")], []);

    private static CandidatesDocument Candidates(DateOnly? asOf, params string[] symbols) => new(
        asOf, Night.AddMinutes(-10), "done", "risk-on", "USD", 20_000m, 3000, 40,
        symbols.Select(s => new CandidateLine(s, $"{s} Inc", "NASDAQ", "breakout", 20m, 19m, 17m, 18m, 50m, 0.25m, 1e9m)).ToList());

    private static async Task<(InMemoryStocksStore Store, FakeNews News)> StoreWith(AdviceDocument advice, CandidatesDocument? candidates = null)
    {
        var store = new InMemoryStocksStore();
        await store.SaveLatestAdviceAsync(advice, CancellationToken.None);
        if (candidates is not null)
            await store.SaveLatestCandidatesAsync(candidates, CancellationToken.None);
        return (store, new FakeNews());
    }

    private static Task<AnalystBrief?> Build(InMemoryStocksStore store, FakeNews news) =>
        new AnalystBriefing(store, news, new MutableTime(Night)).BuildAsync(CancellationToken.None);

    [Fact]
    public async Task TheBriefIsTheOwnersPortfolioWithTheRulesAdviceAndNewsOnEachStock()
    {
        var (store, news) = await StoreWith(Advice(Friday, Owner()), Candidates(Friday, "CAND.US", "NEW.US"));
        news.Failing.Add("KEEP.US");

        var brief = await Build(store, news);

        Assert.NotNull(brief);
        Assert.Equal(Friday, brief.AsOf);
        Assert.Equal(Night, brief.GeneratedAt);
        Assert.True(brief.RealPortfolio);
        // Sizes as fractions of the 20,000 capital, never amounts: 2,000 cash; 10 shares bought at 100, now 110.
        Assert.Equal(0.1m, brief.CashWeight);
        Assert.Collection(brief.Holdings,
            h => Assert.Equal(("OWN.US", "sell", "stop-hit", 0.1m, 0.055m), (h.Symbol, h.RuleAction, h.RuleReason, h.Change, h.Weight)),
            h => Assert.Equal(("KEEP.US", (string?)null), (h.Symbol, h.RuleAction)));
        var buy = Assert.Single(brief.Buys);
        Assert.Equal(("NEW.US", 50m, 45m, 0.012m), (buy.Symbol, buy.Price, buy.Stop, buy.Weight)); // 5 × 50 / 20,000 = 0.0125
        Assert.Equal(["CAND.US", "NEW.US"], brief.Candidates.Select(c => c.Symbol));
        Assert.Equal(0.05m, brief.Candidates[0].Weight); // 50 shares at 20
        Assert.DoesNotContain("20000", JsonSerializer.Serialize(brief, StocksJson.Options), StringComparison.Ordinal);

        // Holdings first, then buys, then candidates; a stock named twice is asked about once; a failure is listed as missing.
        Assert.Equal(["OWN.US", "KEEP.US", "NEW.US", "CAND.US"], news.Asked.Select(a => a.Symbol));
        Assert.All(news.Asked, a => Assert.Equal((Friday.AddDays(-7), 5), (a.From, a.Limit)));
        Assert.Equal(["CAND.US", "NEW.US", "OWN.US"], brief.News.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["KEEP.US"], brief.NewsMissing);
        Assert.Empty(brief.NewsSkipped);
    }

    [Fact]
    public async Task BeforeTheOwnerSentAPortfolioTheBriefIsThePaperOneAndOldCandidatesAreLeftOut()
    {
        var (store, news) = await StoreWith(Advice(Friday), Candidates(Friday.AddDays(-1), "OLD.US"));

        var brief = await Build(store, news);

        Assert.NotNull(brief);
        Assert.False(brief.RealPortfolio);
        Assert.Equal(0.4m, brief.CashWeight);
        Assert.Equal(["PAPER.US"], brief.Holdings.Select(h => h.Symbol));
        Assert.Equal(["PAPERBUY.US"], brief.Buys.Select(b => b.Symbol));
        Assert.Empty(brief.Candidates);
        Assert.Equal(["PAPER.US", "PAPERBUY.US"], news.Asked.Select(a => a.Symbol));
    }

    [Fact]
    public async Task ASymbolTheNewsSourceRefusesIsMissingNotAFailedBrief()
    {
        var (store, _) = await StoreWith(Advice(Friday));
        var news = new FakeNews();
        news.Failing.Add("PAPER.US");
        var refusing = new RefusingNews(news, "PAPERBUY.US");

        var brief = await new AnalystBriefing(store, refusing, new MutableTime(Night)).BuildAsync(CancellationToken.None);

        Assert.NotNull(brief);
        Assert.Equal(["PAPER.US", "PAPERBUY.US"], brief.NewsMissing);
    }

    private sealed class RefusingNews(INewsSource inner, string symbol) : INewsSource
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string s, DateOnly from, int limit, CancellationToken cancellationToken) =>
            s == symbol ? throw new ArgumentException("The symbol must be TICKER.EXCHANGE.") : inner.GetNewsAsync(s, from, limit, cancellationToken);
    }

    [Fact]
    public async Task NewsIsFetchedForAtMostThirtyStocksAndTheRestAreSkipped()
    {
        var symbols = Enumerable.Range(1, 35).Select(i => $"C{i:00}.US").ToArray();
        var (store, news) = await StoreWith(Advice(Friday), Candidates(Friday, symbols));

        var brief = await Build(store, news);

        Assert.NotNull(brief);
        Assert.Equal(AnalystBriefing.MaxNewsSymbols, news.Asked.Count);
        Assert.Equal(AnalystBriefing.MaxNewsSymbols, brief.News.Count);
        Assert.Empty(brief.NewsMissing);
        Assert.Equal(37 - AnalystBriefing.MaxNewsSymbols, brief.NewsSkipped.Count); // the paper holding, its buy and 35 candidates
        Assert.Equal("C35.US", brief.NewsSkipped[^1]);
    }

    [Fact]
    public async Task NoBriefWithoutAdviceOrWhenItsCloseIsAnalysed()
    {
        var empty = new InMemoryStocksStore();
        Assert.Null(await Build(empty, new FakeNews()));

        var (store, news) = await StoreWith(Advice(Friday));
        await store.SaveLatestAnalysisAsync(AnalysisDocument.Missing(Friday, Night, AnalysisDocument.NotRun, "usage-limit"), CancellationToken.None);
        Assert.NotNull(await Build(store, news)); // a run that didn't use Claude is tried again

        await store.SaveLatestAnalysisAsync(Done(Friday), CancellationToken.None);
        Assert.Null(await Build(store, news));
        Assert.NotNull(await Build(await Then(store, Advice(Friday.AddDays(3))), news)); // Monday's close is new
    }

    [Fact]
    public async Task TheAnalystTakesTheStoredBriefOnlyForTheLatestAdviceAndOnlyUntilItIsAnalysed()
    {
        var (store, news) = await StoreWith(Advice(Friday));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None)); // no brief yet

        await store.SaveBriefAsync((await Build(store, news))!, CancellationToken.None);
        Assert.Equal(Friday, (await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None))?.AsOf);

        await store.SaveLatestAnalysisAsync(AnalysisDocument.Missing(Friday, Night, AnalysisDocument.NotRun, "usage-limit"), CancellationToken.None);
        Assert.NotNull(await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None)); // the second slot tries again

        await store.SaveLatestAnalysisAsync(Done(Friday), CancellationToken.None);
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None));

        // Monday's advice without Monday's brief (its news failed): Friday's brief isn't analysed for it.
        await store.SaveLatestAdviceAsync(Advice(Friday.AddDays(3)), CancellationToken.None);
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None));
    }

    [Fact]
    public async Task NothingIsAnalysedWhileStocksAreStoppedAndAfterStartOnlyTheNextNightsBrief()
    {
        var (store, news) = await StoreWith(Advice(Friday));
        var brief = (await Build(store, news))!; // written at Night
        await store.SaveBriefAsync(brief, CancellationToken.None);

        // Stopped after the nightly run wrote the brief: neither the night's pass nor the check before the open runs.
        store.Press(StocksSwitch.Pause, Night.AddMinutes(5));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night.AddMinutes(10), CancellationToken.None));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen, CancellationToken.None));

        // Started again: the brief from before the stop is never analysed, not even by a check before the open.
        store.Press(StocksSwitch.Resume, Night.AddHours(1));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night.AddHours(2), CancellationToken.None));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen, CancellationToken.None));

        // The first nightly run after the Start writes the next brief: that one is analysed.
        await store.SaveBriefAsync(brief with { GeneratedAt = Night.AddHours(3) }, CancellationToken.None);
        Assert.Equal(AnalystPass.Night, (await AnalystBriefing.ToAnalyseAsync(store, Night.AddHours(3), CancellationToken.None))?.Pass);
    }

    /// <summary>News that takes a minute a request, on the test's clock.</summary>
    private sealed class SlowNews(INewsSource inner, MutableTime time) : INewsSource
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, DateOnly from, int limit, CancellationToken cancellationToken)
        {
            time.Now = time.Now.AddMinutes(1);
            return inner.GetNewsAsync(symbol, from, limit, cancellationToken);
        }
    }

    [Fact]
    public async Task TheNightlyBriefStopsFetchingNewsAtItsTimeAndIsSavedWithWhatItHas()
    {
        var (store, news) = await StoreWith(Advice(Friday, Owner()), Candidates(Friday, "CAND.US"));
        var time = new MutableTime(Night);
        var line = await new AnalystBriefing(store, new SlowNews(news, time), time).WriteAsync(Night.AddMinutes(2), CancellationToken.None);

        var brief = await store.GetBriefAsync(CancellationToken.None);
        Assert.NotNull(brief);
        Assert.Equal(["KEEP.US", "OWN.US"], brief.News.Keys.Order(StringComparer.Ordinal)); // the holdings first
        Assert.Equal(["NEW.US", "CAND.US"], brief.NewsSkipped); // not fetched: out of time
        Assert.Contains("news on 2", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANightlyBriefThatFailsIsALogLineAndNoBrief()
    {
        var (store, _) = await StoreWith(Advice(Friday, Owner()));
        var line = await new AnalystBriefing(store, new BrokenNews(), new MutableTime(Night)).WriteAsync(Night.AddMinutes(5), CancellationToken.None);

        Assert.StartsWith("Analyst brief failed (the advice stands): HttpRequestException", line, StringComparison.Ordinal);
        Assert.Null(await store.GetBriefAsync(CancellationToken.None));
        Assert.Equal("Analyst brief: nothing new to analyse.", await new AnalystBriefing(new InMemoryStocksStore(), new BrokenNews(), new MutableTime(Night)).WriteAsync(Night, CancellationToken.None));
    }

    private sealed class BrokenNews : INewsSource
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, DateOnly from, int limit, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection reset");
    }

    private static async Task<InMemoryStocksStore> Then(InMemoryStocksStore store, AdviceDocument advice)
    {
        await store.SaveLatestAdviceAsync(advice, CancellationToken.None);
        return store;
    }

    private static AnalysisDocument Done(DateOnly asOf, string? pass = null, params string[] picks) =>
        new(asOf, Night, AnalysisDocument.Done, null, "calm", [], picks.Select((p, i) => new PickView(p, i + 1, "why", null)).ToList(), [], 0, Pass: pass);

    // Monday 2 November 2026, 13:40 UTC: 08:40 in New York (winter time), before the open; Friday's close is still the latest advice.
    private static readonly DateTimeOffset BeforeTheOpen = new(2026, 11, 2, 13, 40, 0, TimeSpan.Zero);

    private static AnalysisDocument Checked(DateOnly asOf, DateTimeOffset at) => Done(asOf, AnalystPass.PreOpen) with { GeneratedAt = at };

    [Theory]
    [InlineData("2026-10-31T02:30:00Z", AnalystPass.Night)] // Saturday night, after Friday's close
    [InlineData("2026-11-03T02:40:00Z", AnalystPass.Night)]
    [InlineData("2026-11-03T06:40:00Z", AnalystPass.Night)]
    [InlineData("2026-11-02T12:40:00Z", AnalystPass.Night)] // 07:40 EST: before the 8:30 data
    [InlineData("2026-11-02T13:40:00Z", AnalystPass.PreOpen)] // 08:40 EST
    [InlineData("2026-11-02T13:34:00Z", AnalystPass.Night)] // 08:34
    [InlineData("2026-11-02T13:35:00Z", AnalystPass.PreOpen)] // 08:35
    [InlineData("2026-11-02T14:29:00Z", AnalystPass.PreOpen)] // 09:29
    [InlineData("2026-11-02T14:30:00Z", AnalystPass.Night)] // the open
    [InlineData("2026-10-30T12:40:00Z", AnalystPass.PreOpen)] // Friday, 08:40 EDT
    [InlineData("2026-10-30T13:40:00Z", AnalystPass.Night)] // 09:40 EDT: after the open
    [InlineData("2026-03-06T13:40:00Z", AnalystPass.PreOpen)] // Friday before daylight time (8 March 2026): 08:40 EST
    [InlineData("2026-03-09T12:40:00Z", AnalystPass.PreOpen)] // Monday after it: 08:40 EDT
    [InlineData("2026-03-09T13:40:00Z", AnalystPass.Night)]
    [InlineData("2026-10-31T12:40:00Z", AnalystPass.Night)] // Saturday: no session, so the night's pass (which skips a close done)
    [InlineData("2026-11-02T15:40:00+02:00", AnalystPass.PreOpen)] // 13:40 UTC
    public void ThePassFollowsNewYorkTime(string at, string pass) =>
        Assert.Equal(pass, AnalystPass.For(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2026-03-08T06:59:00Z", "2026-03-08T01:59:00")] // daylight time starts at 02:00 EST, 07:00 UTC
    [InlineData("2026-03-08T07:00:00Z", "2026-03-08T03:00:00")]
    [InlineData("2026-11-01T05:59:00Z", "2026-11-01T01:59:00")] // and ends at 02:00 EDT, 06:00 UTC
    [InlineData("2026-11-01T06:00:00Z", "2026-11-01T01:00:00")]
    [InlineData("2027-03-14T07:00:00Z", "2027-03-14T03:00:00")] // 14 March 2027 is that year's second Sunday
    public void NewYorkTimeFollowsTheUsDaylightRule(string utc, string local) =>
        Assert.Equal(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture),
            AnalystPass.NewYork(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task TheCheckBeforeTheOpenGetsTheNightsViewAndRunsOncePerNewYorkDay()
    {
        var (store, news) = await StoreWith(Advice(Friday));
        await store.SaveBriefAsync((await Build(store, news))!, CancellationToken.None);
        Assert.Equal(AnalystPass.Night, (await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None))?.Pass);

        // The night's run didn't use Claude: the check before the open is a full analysis, with no night view.
        await store.SaveLatestAnalysisAsync(AnalysisDocument.Missing(Friday, Night, AnalysisDocument.NotRun, "usage-limit"), CancellationToken.None);
        var first = await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen, CancellationToken.None);
        Assert.Equal((AnalystPass.PreOpen, (AnalysisDocument?)null), (first?.Pass, first?.Night));

        // The night's done (an older document has no pass: the night's): the night's pass has nothing left, the check gets its view.
        await store.SaveLatestAnalysisAsync(Done(Friday, null, "PAPERBUY.US"), CancellationToken.None);
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night.AddHours(4), CancellationToken.None));
        var check = await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen, CancellationToken.None);
        Assert.Equal((AnalystPass.PreOpen, (DateOnly?)Friday), (check?.Pass, check?.Night?.AsOf));
        Assert.Equal(["PAPERBUY.US"], check!.Night!.Picks.Select(p => p.Symbol));

        // Once checked, nothing is left that New York day for Friday's close.
        await store.SaveLatestAnalysisAsync(Checked(Friday, BeforeTheOpen), CancellationToken.None);
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen.AddMinutes(30), CancellationToken.None));
        Assert.Null(await AnalystBriefing.ToAnalyseAsync(store, Night, CancellationToken.None));

        // A holiday on Monday: no new close by Tuesday, whose open gets its own check, of the Monday check's view.
        var tuesday = await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen.AddDays(1), CancellationToken.None);
        Assert.Equal((AnalystPass.PreOpen, (string?)AnalystPass.PreOpen), (tuesday?.Pass, tuesday?.Night?.Pass));

        // A check of an older close is no view of this one.
        await store.SaveLatestAnalysisAsync(Checked(Friday.AddDays(-1), BeforeTheOpen.AddDays(-3)), CancellationToken.None);
        Assert.Null((await AnalystBriefing.ToAnalyseAsync(store, BeforeTheOpen, CancellationToken.None))!.Night);
    }

    private const string MorningAnswer = """
        ```json
        { "market": "Futures down on a jobs report.",
          "holdings": [{ "symbol": "OWN.US", "action": "sell-all", "reason": "Cut its guidance before the bell." }],
          "picks": [{ "symbol": "CAND.US", "rank": 1, "reason": "New." }, { "symbol": "NEW.US", "rank": 2, "reason": "Still fine." }],
          "skips": [{ "symbol": "MORE.US", "reason": "Reports today." }] }
        ```
        """;

    [Fact]
    public void BeforeTheOpenOnlyTheNightsPicksCanBeKept()
    {
        var night = Done(Friday, AnalystPass.Night, "NEW.US", "MORE.US");

        var check = AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen, Night = night }, MorningAnswer, BeforeTheOpen);

        Assert.Equal((AnalysisDocument.Done, AnalystPass.PreOpen), (check.Status, check.Pass));
        Assert.Equal(["NEW.US"], check.Picks.Select(p => p.Symbol)); // CAND.US wasn't a pick of the night's
        Assert.Equal(1, check.Picks[0].Rank);
        Assert.Equal(["MORE.US"], check.Skips.Select(k => k.Symbol));
        Assert.Equal(1, check.Dropped);
        Assert.Equal(AnalystActions.SellAll, Assert.Single(check.Holdings).Action);

        // Without a night's view (it didn't run), the check is a full analysis.
        var full = AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen }, MorningAnswer, BeforeTheOpen);
        Assert.Equal(["CAND.US", "NEW.US"], full.Picks.Select(p => p.Symbol));
        Assert.Equal(AnalystPass.PreOpen, AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen }, "no json", BeforeTheOpen).Pass);
    }

    [Fact]
    public void BeforeTheOpenTheNightsWarningsStand()
    {
        var earnings = new DateOnly(2026, 11, 5);
        var night = Done(Friday, AnalystPass.Night) with
        {
            Holdings = [new("OWN.US", AnalystActions.SellAll, "Fraud report.", earnings), new("KEEP.US", AnalystActions.SellHalf, "Reports on Thursday.", earnings)],
            Skips = [new("CAND.US", "A rumour spike.", null), new("GONE.US", "Not in the brief.", null)],
        };
        const string answer = """
            { "holdings": [{ "symbol": "OWN.US", "action": "hold", "reason": "The report was denied." }], "picks": [] }
            """;

        var check = AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen, Night = night }, answer, BeforeTheOpen);

        Assert.Equal(AnalysisDocument.Done, check.Status);
        // OWN.US can't go from sell-all to hold; KEEP.US, left out, keeps the night's line; both keep the earnings day.
        Assert.Equal([("OWN.US", AnalystActions.SellAll, "Fraud report."), ("KEEP.US", AnalystActions.SellHalf, "Reports on Thursday.")],
            check.Holdings.Select(h => (h.Symbol, h.Action, h.Reason)));
        Assert.All(check.Holdings, h => Assert.Equal(earnings, h.Earnings));
        Assert.Equal(1, check.Dropped);
        Assert.Equal(["CAND.US"], check.Skips.Select(k => k.Symbol)); // a skip of a stock the brief no longer has isn't carried

        // More caution is taken as written, and the night's earnings day fills a line that has none.
        var sooner = AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen, Night = night },
            """{ "holdings": [{ "symbol": "KEEP.US", "action": "sell-all", "reason": "Missed." }], "picks": [] }""", BeforeTheOpen);
        Assert.Equal(("KEEP.US", AnalystActions.SellAll, (DateOnly?)earnings), (sooner.Holdings[0].Symbol, sooner.Holdings[0].Action, sooner.Holdings[0].Earnings));

        // An answer with no line of its own is still empty: the night's lines don't make it a check.
        Assert.Equal((AnalysisDocument.Refused, "empty"), (AnalystAnswer.Check(Brief() with { Pass = AnalystPass.PreOpen, Night = night },
            """{ "holdings": [], "picks": [] }""", BeforeTheOpen) is var empty ? (empty.Status, empty.Reason) : default));
    }

    [Fact]
    public void ThePassAndTheNightsViewRoundTripThroughTheBriefsFile()
    {
        var brief = Brief() with { Pass = AnalystPass.PreOpen, Night = Done(Friday, AnalystPass.Night, "NEW.US") };

        var json = JsonSerializer.Serialize(brief, StocksJson.Options);
        var back = JsonSerializer.Deserialize<AnalystBrief>(json, StocksJson.Options)!;

        Assert.Contains("\"pass\":\"pre-open\"", json, StringComparison.Ordinal); // run.sh reads it with jq
        Assert.Equal((AnalystPass.PreOpen, AnalystPass.Night, "NEW.US"), (back.Pass, back.Night?.Pass, back.Night?.Picks[0].Symbol));
    }

    [Fact]
    public async Task TheCheckBeforeTheOpenReplacesTheNightsViewAndIsNeverReplacedByIt()
    {
        var store = new InMemoryStocksStore();
        Assert.True(await AnalysisRecord.SaveAsync(store, Done(Friday, AnalystPass.Night), CancellationToken.None));
        Assert.True(await AnalysisRecord.SaveAsync(store, Checked(Friday, BeforeTheOpen), CancellationToken.None));
        Assert.True(store.Documents.ContainsKey("analysis/analysis-2026-10-30"));
        Assert.True(store.Documents.ContainsKey("analysis/analysis-2026-10-30-pre-open-2026-11-02"));
        Assert.Equal(AnalystPass.PreOpen, store.Read<AnalysisDocument>("analysis/analysis-latest")!.Pass);

        // A failed check, or a late night's run, keeps the check.
        Assert.False(await AnalysisRecord.SaveAsync(store, AnalysisDocument.Missing(Friday, BeforeTheOpen, AnalysisDocument.NotRun, "timeout", AnalystPass.PreOpen), CancellationToken.None));
        Assert.False(await AnalysisRecord.SaveAsync(store, Done(Friday, AnalystPass.Night) with { GeneratedAt = BeforeTheOpen }, CancellationToken.None));
        // Even when it read the latest before the check was saved: the check's dated copy stops it.
        await store.SaveLatestAnalysisAsync(Done(Friday, AnalystPass.Night), CancellationToken.None);
        Assert.False(await AnalysisRecord.SaveAsync(store, Done(Friday, AnalystPass.Night) with { GeneratedAt = BeforeTheOpen, Market = "late" }, CancellationToken.None));
        Assert.False(await AnalysisRecord.SaveAsync(store, AnalysisDocument.Missing(Friday, BeforeTheOpen, AnalysisDocument.Refused, "unreadable", AnalystPass.PreOpen), CancellationToken.None));
        // A second check that day (an overlapping run) keeps the first.
        Assert.False(await AnalysisRecord.SaveAsync(store, Checked(Friday, BeforeTheOpen.AddMinutes(5)) with { Market = "second" }, CancellationToken.None));

        // The next day's check (Monday was a holiday) is its own.
        Assert.True(await AnalysisRecord.SaveAsync(store, Checked(Friday, BeforeTheOpen.AddDays(1)), CancellationToken.None));
        Assert.True(store.Documents.ContainsKey("analysis/analysis-2026-10-30-pre-open-2026-11-03"));
    }

    private static AnalystBrief Brief() => new(
        Friday, Night, "risk-on", "VTI.US", true, 0.1m,
        [new("OWN.US", "Own", 110m, 0.1m, 0.055m, 90m, new DateOnly(2026, 9, 1), null, null),
         new("KEEP.US", "Keep", 55m, 0.1m, 0.014m, 45m, new DateOnly(2026, 9, 1), "raise-stop", "trail")],
        [new("NEW.US", "New", 50m, 45m, 0.012m, "breakout")],
        [new("CAND.US", "Cand", "breakout", 20m, 19m, 18m, 0.05m, 0.25m), new("MORE.US", "More", "breakout", 20m, 19m, 18m, 0.05m, 0.2m)],
        new Dictionary<string, IReadOnlyList<NewsItem>>(), [], []);

    [Fact]
    public void AnAnswerIsKeptOnlyForTheBriefsStocksAndAllowedActions()
    {
        const string answer = """
            Here is my view.
            ```json
            {
              "market": "Calm before\nthe Fed.",
              "marketRu": "Спокойно перед ФРС.",
              "holdings": [
                { "symbol": "OWN.US", "action": "sell-half", "reason": "Earnings next week, guidance cut.", "earnings": "2026-11-05", "reasonRu": "Отчёт на\tследующей неделе." },
                { "symbol": "OWN.US", "action": "hold", "reason": "Second line for the same stock." },
                { "symbol": "KEEP.US", "action": "double-up", "reason": "Not an allowed action." },
                { "symbol": "ELSE.US", "action": "hold", "reason": "Not held." }
              ],
              "picks": [
                { "symbol": "CAND.US", "rank": 2, "reason": "Strong sector.", "earnings": "2027-03-01" },
                { "symbol": "NEW.US", "rank": 1, "reason": "Clean breakout.", "earnings": "2026-10-01" },
                { "symbol": "ELSE.US", "rank": 3, "reason": "Not in the brief." },
                { "symbol": "MORE.US", "rank": 4, "reason": "   " }
              ],
              "skips": [
                { "symbol": "MORE.US", "reason": "Lawsuit pending." },
                { "symbol": "CAND.US", "reason": "Also a pick." }
              ]
            }
            ```
            """;

        var analysis = AnalystAnswer.Check(Brief(), answer, Night);

        Assert.Equal((AnalysisDocument.Done, (string?)null), (analysis.Status, analysis.Reason));
        Assert.Equal((Friday, Night), (analysis.AsOf, analysis.GeneratedAt));
        Assert.Equal("Calm before the Fed.", analysis.Market);
        Assert.Equal("Спокойно перед ФРС.", analysis.MarketRu);
        var holding = Assert.Single(analysis.Holdings);
        Assert.Equal("Отчёт на следующей неделе.", holding.ReasonRu);
        Assert.Equal(("OWN.US", AnalystActions.SellHalf, "Earnings next week, guidance cut.", (DateOnly?)new DateOnly(2026, 11, 5)),
            (holding.Symbol, holding.Action, holding.Reason, holding.Earnings));
        // Ordered by the analyst's rank and renumbered; an earnings date in the past or beyond 60 days is left out.
        Assert.Collection(analysis.Picks,
            p => Assert.Equal(("NEW.US", 1, (DateOnly?)null), (p.Symbol, p.Rank, p.Earnings)),
            p => Assert.Equal(("CAND.US", 2, (DateOnly?)null), (p.Symbol, p.Rank, p.Earnings)));
        var skip = Assert.Single(analysis.Skips);
        Assert.Equal(("MORE.US", "Lawsuit pending."), (skip.Symbol, skip.Reason));
        Assert.Equal(6, analysis.Dropped);
    }

    [Fact]
    public void AtMostFivePicksAreKeptAndTextIsCleanedAndCut()
    {
        var candidates = Enumerable.Range(1, 7).Select(i => new BriefCandidate($"C{i}.US", null, "breakout", 20m, 19m, 18m, 0.05m, 0.2m)).ToList();
        var brief = Brief() with { Candidates = candidates };
        var picks = string.Join(",", candidates.Select((c, i) => $$"""{ "symbol": "{{c.Symbol}}", "rank": "{{i + 1}}", "reason": "r{{i}}" }"""));
        var longReason = "‮evil​ " + new string('é', 700);
        var answer = $$"""{ "market": {{JsonSerializer.Serialize(longReason)}}, "holdings": [{ "symbol": "OWN.US", "action": "hold", "reason": "ok" }], "picks": [{{picks}}], "earnings": "ignored" }""";

        var analysis = AnalystAnswer.Check(brief, answer, Night);

        Assert.Equal(AnalystAnswer.MaxPicks, analysis.Picks.Count);
        Assert.Equal(["C1.US", "C2.US", "C3.US", "C4.US", "C5.US"], analysis.Picks.Select(p => p.Symbol));
        Assert.Equal(2, analysis.Dropped);
        Assert.NotNull(analysis.Market);
        Assert.StartsWith("evil é", analysis.Market);
        Assert.Equal(600, analysis.Market.Length);
        Assert.Single(analysis.Holdings);
        Assert.Empty(analysis.Skips);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I couldn't finish the analysis.")]
    [InlineData("{ not json }")]
    [InlineData("""{ "market": { "a": { "b": { "c": { "d": { "e": { "f": { "g": { "h": 1 } } } } } } } } }""")]
    [InlineData("[1, 2]")]
    public void AnAnswerThatIsntTheObjectIsRefused(string answer)
    {
        var analysis = AnalystAnswer.Check(Brief(), answer, Night);

        Assert.Equal((AnalysisDocument.Refused, "unreadable"), (analysis.Status, analysis.Reason));
        Assert.Equal(Friday, analysis.AsOf);
        Assert.Empty(analysis.Holdings);
        Assert.Empty(analysis.Picks);
    }

    [Theory]
    [InlineData("{}", "unreadable")]
    [InlineData("""{ "error": "I couldn't read the news." }""", "unreadable")]
    [InlineData("""{ "type": "result", "is_error": true, "result": "Claude usage limit reached" }""", "unreadable")]
    [InlineData("""{ "holdings": "none" }""", "unreadable")]
    [InlineData("""{ "holdings": [ { "symbol": "OWN.US", "action": "hold", "reason": 5 }, "OWN.US", { "symbol": "ELSE.US" } ], "picks": [] }""", "empty")]
    [InlineData("""{ "market": "Calm.", "holdings": [], "picks": [ { "symbol": "NEW.US", "rank": 1, "reason": "Fine." } ] }""", "empty")]
    public void AnAnswerWithNothingForTheBriefIsRefusedNotAnEmptyDone(string answer, string reason)
    {
        var analysis = AnalystAnswer.Check(Brief(), answer, Night);

        Assert.Equal((AnalysisDocument.Refused, reason), (analysis.Status, analysis.Reason));
    }

    [Fact]
    public void AMalformedLineIsDroppedNotTheAnswerAndAFenceIsPreferred()
    {
        const string answer = """
            ```json
            { "holdings": [ { "symbol": "OWN.US", "action": "hold", "reason": "Fine.", "earnings": 20261105 },
                            { "symbol": "KEEP.US", "action": "hold", "reason": ["a", "b"] } ],
              "picks": [ { "symbol": "CAND.US", "rank": 1.5, "reason": "Half a rank." },
                         { "symbol": "MORE.US", "reason": "Unranked, so last." },
                         { "symbol": "NEW.US", "rank": "1", "reason": "Ranked as text." } ] }
            ```
            Hope this helps :}
            """;

        var analysis = AnalystAnswer.Check(Brief(), answer, Night);

        Assert.Equal(AnalysisDocument.Done, analysis.Status);
        var holding = Assert.Single(analysis.Holdings);
        Assert.Equal(("OWN.US", (DateOnly?)null), (holding.Symbol, holding.Earnings));
        Assert.Equal(["NEW.US", "MORE.US"], analysis.Picks.Select(p => p.Symbol));
        Assert.Equal(2, analysis.Dropped);
    }

    [Fact]
    public void AnOversizedAnswerIsRefused()
    {
        var answer = $$"""{ "market": "{{new string('a', AnalystAnswer.MaxBytes)}}" }""";

        Assert.Equal(AnalysisDocument.Refused, AnalystAnswer.Check(Brief(), answer, Night).Status);
    }

    [Fact]
    public async Task ADoneAnalysisIsNeverReplacedByAFailedRunOrAnOlderClose()
    {
        var store = new InMemoryStocksStore();
        var notRun = AnalysisDocument.Missing(Friday, Night, AnalysisDocument.NotRun, "usage-limit");
        Assert.True(await AnalysisRecord.SaveAsync(store, notRun, CancellationToken.None));
        Assert.False(store.Documents.ContainsKey("analysis/analysis-2026-10-30")); // only a done analysis is kept by date

        Assert.True(await AnalysisRecord.SaveAsync(store, Done(Friday), CancellationToken.None));
        Assert.True(store.Documents.ContainsKey("analysis/analysis-2026-10-30"));

        Assert.False(await AnalysisRecord.SaveAsync(store, notRun with { GeneratedAt = Night.AddHours(1) }, CancellationToken.None));
        Assert.False(await AnalysisRecord.SaveAsync(store, Done(Friday.AddDays(-1)), CancellationToken.None));
        Assert.Equal(AnalysisDocument.Done, store.Read<AnalysisDocument>("analysis/analysis-latest")!.Status);

        // A run that overlapped the done one read the latest before it was saved: the dated copy still stops it.
        await store.SaveLatestAnalysisAsync(notRun, CancellationToken.None);
        Assert.False(await AnalysisRecord.SaveAsync(store, notRun with { Reason = "timeout" }, CancellationToken.None));

        var monday = AnalysisDocument.Missing(Friday.AddDays(3), Night.AddDays(3), AnalysisDocument.Refused, "unreadable");
        Assert.True(await AnalysisRecord.SaveAsync(store, monday, CancellationToken.None));
        Assert.Equal(AnalysisDocument.Refused, store.Read<AnalysisDocument>("analysis/analysis-latest")!.Status);
    }

    [Fact]
    public void TheBriefRoundTripsThroughItsFile()
    {
        var brief = Brief() with
        {
            News = new Dictionary<string, IReadOnlyList<NewsItem>> { ["OWN.US"] = [new(Night, "Title", "Summary", "reuters.com", -0.25m)] },
            NewsMissing = ["NEW.US"],
        };

        var json = JsonSerializer.Serialize(brief, StocksJson.Options);
        var back = JsonSerializer.Deserialize<AnalystBrief>(json, StocksJson.Options)!;

        Assert.Equal(brief.Holdings, back.Holdings);
        Assert.Equal(brief.Candidates, back.Candidates);
        Assert.Equal(-0.25m, back.News["OWN.US"][0].Polarity);
        Assert.Equal(["NEW.US"], back.NewsMissing);
        Assert.Contains("\"cashWeight\":\"0.1\"", json, StringComparison.Ordinal);
    }
}
