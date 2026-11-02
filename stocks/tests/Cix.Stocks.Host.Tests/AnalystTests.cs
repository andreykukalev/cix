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

    private static async Task<InMemoryStocksStore> Then(InMemoryStocksStore store, AdviceDocument advice)
    {
        await store.SaveLatestAdviceAsync(advice, CancellationToken.None);
        return store;
    }

    private static AnalysisDocument Done(DateOnly asOf) => new(asOf, Night, AnalysisDocument.Done, null, "calm", [], [], [], 0);

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
        Assert.False(store.Documents.ContainsKey("advice/analysis-2026-10-30")); // only a done analysis is kept by date

        Assert.True(await AnalysisRecord.SaveAsync(store, Done(Friday), CancellationToken.None));
        Assert.True(store.Documents.ContainsKey("advice/analysis-2026-10-30"));

        Assert.False(await AnalysisRecord.SaveAsync(store, notRun with { GeneratedAt = Night.AddHours(1) }, CancellationToken.None));
        Assert.False(await AnalysisRecord.SaveAsync(store, Done(Friday.AddDays(-1)), CancellationToken.None));
        Assert.Equal(AnalysisDocument.Done, store.Read<AnalysisDocument>("advice/analysis-latest")!.Status);

        // A run that overlapped the done one read the latest before it was saved: the dated copy still stops it.
        await store.SaveLatestAnalysisAsync(notRun, CancellationToken.None);
        Assert.False(await AnalysisRecord.SaveAsync(store, notRun with { Reason = "timeout" }, CancellationToken.None));

        var monday = AnalysisDocument.Missing(Friday.AddDays(3), Night.AddDays(3), AnalysisDocument.Refused, "unreadable");
        Assert.True(await AnalysisRecord.SaveAsync(store, monday, CancellationToken.None));
        Assert.Equal(AnalysisDocument.Refused, store.Read<AnalysisDocument>("advice/analysis-latest")!.Status);
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
