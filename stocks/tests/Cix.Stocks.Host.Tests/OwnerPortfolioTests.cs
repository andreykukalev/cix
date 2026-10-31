namespace Cix.Stocks.Host.Tests;

public class OwnerPortfolioTests
{
    // Wednesday 28 October 2026's close, run at 02:00 UTC on Thursday. XYZ and OWN rise 0.1 a day to 142.2 with an ATR of 1;
    // ABC falls; the benchmark VTI rises, so the market is risk-on.
    private static readonly DateOnly Wednesday = new(2026, 10, 28);
    private static readonly DateTimeOffset ThursdayRun = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private sealed class Setup
    {
        public StocksConfig Config { get; } = new StocksConfig
        {
            BaseCurrency = "USD",
            Capital = 10_000m,
            Benchmark = "VTI.US",
            MaxCallsPerDay = 18,
            Watchlist = [new("XYZ.US", "XYZ Corp", "USD", "tech"), new("ABC.US", "ABC Corp", "USD")],
        }.Validate();

        public FakeBars Source { get; } = new() { LastDay = Wednesday };
        public InMemoryStocksStore Store { get; } = new();
        public MutableTime Time { get; } = new(ThursdayRun);

        public Task<RunReport> RunAsync() => new DailyRun(Config, Source, Store, Time).RunAsync(CancellationToken.None);

        public AdviceDocument Latest => Store.Read<AdviceDocument>("advice/advice-latest")!;

        public OwnerState? Owner => Store.Read<OwnerState>("state/owner");

        public void NextDay()
        {
            Source.LastDay = Source.LastDay.AddDays(1);
            Time.Now = Time.Now.AddDays(1);
        }
    }

    private static OwnerHoldingInput Held(string symbol, decimal quantity, decimal cost, decimal? stop = null, DateOnly? opened = null, string? name = null) =>
        new(symbol, name, quantity, cost, stop, opened ?? new DateOnly(2026, 9, 1));

    private static PortfolioSubmission Submission(int sequence, decimal capital, params OwnerHoldingInput[] holdings) =>
        new(sequence, Sent, capital, holdings);

    [Fact]
    public async Task Run_TheOwnersPortfolioIsAdvisedOnItsOwnCapitalBesideThePaperOne()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m, Held("XYZ.US", 10m, 120m), Held("OWN.US", 5m, 100m, name: "Own Inc")));

        var report = await s.RunAsync();

        // The benchmark, the owner's two holdings and ABC: OWN isn't on the watchlist but is fetched for its stop.
        Assert.Equal(4, report.Calls);
        var owner = s.Latest.Owner!;
        // 5000 minus what the holdings cost (1200 + 500); worth 15 × 142.2 at the close.
        Assert.Equal((5_000m, 3_300m, 1_700m, 2_133m, 1), (owner.Capital, owner.Cash, owner.Invested, owner.Value, owner.AppliedSequence));
        // Neither has a stop: each gets one 3 ATRs under its highest close since it was bought. Nothing to buy: ABC falls and
        // XYZ is held.
        Assert.Equal(
            [new ActionLine("set-stop", "OWN.US", "Own Inc", "USD", 5m, 142.2m, 139.2m, Reasons.NoStop), new ActionLine("set-stop", "XYZ.US", "XYZ Corp", "USD", 10m, 142.2m, 139.2m, Reasons.NoStop)],
            owner.Actions.OrderBy(a => a.Symbol, StringComparer.Ordinal));
        Assert.Equal(["XYZ.US", "OWN.US"], owner.Holdings.Select(h => h.Symbol));
        Assert.All(owner.Holdings, h => Assert.Null(h.Stop)); // the page shows the stop as it was, beside the advice

        // The paper portfolio is advised as before (it holds nothing: XYZ's breakout is a buy), and knows nothing of OWN.
        Assert.Equal("buy", Assert.Single(s.Latest.Actions).Kind);
        Assert.DoesNotContain(s.Latest.Notes, n => n.Symbol == "OWN.US");
        // The watchlist says what the owner holds.
        Assert.Equal("held", s.Latest.Watchlist!.Single(w => w.Symbol == "XYZ.US").Status);

        // The stops advised are taken as placed, and the terms of the bars kept for the next split.
        var saved = s.Owner!;
        Assert.Equal(1, saved.AppliedSequence);
        Assert.All(saved.Holdings, h => Assert.Equal(139.2m, h.Stop));
        Assert.Equal(["OWN.US", "XYZ.US"], saved.Terms!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(s.Latest.Refusal);
    }

    [Fact]
    public async Task Run_ABuyIsSizedOnTheOwnersCapital()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m));

        await s.RunAsync();

        // 1% of 5000 over 3 + 1 ATR is 12 shares; the 20% cap, 1000 / 142.2, is 7.
        var buy = Assert.Single(s.Latest.Owner!.Actions);
        Assert.Equal(new ActionLine("buy", "XYZ.US", "XYZ Corp", "USD", 7m, 142.2m, 139.2m, Reasons.Breakout), buy);
        Assert.Equal((5_000m, 0m, 0m), (s.Latest.Owner.Cash, s.Latest.Owner.Invested, s.Latest.Owner.Value));
        // Paper still sizes on its own 10 000.
        Assert.Equal(14m, Assert.Single(s.Latest.Actions).Quantity);
    }

    [Fact]
    public async Task Run_WithoutASubmissionThereIsNoOwnersSection()
    {
        var s = new Setup();

        await s.RunAsync();

        Assert.Null(s.Latest.Owner);
        Assert.Null(s.Owner);
        Assert.Equal("breakout", s.Latest.Watchlist!.Single(w => w.Symbol == "XYZ.US").Status);
    }

    [Fact]
    public async Task Run_TheNewestSubmissionWinsAndARefusedOneLeavesThePortfolioAsItWas()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m));
        await s.RunAsync();

        // Two sent before the next run: the newer stands.
        s.NextDay();
        s.Store.Submit(Submission(2, 4_000m));
        s.Store.Submit(Submission(3, 3_000m));
        await s.RunAsync();
        Assert.Equal((3_000m, 3), (s.Latest.Owner!.Capital, s.Latest.Owner.AppliedSequence));

        // A refused one: the portfolio stays, the refusal is shown, and it isn't looked at again.
        s.NextDay();
        s.Store.Submit(Submission(4, 0m));
        await s.RunAsync();
        Assert.Equal(new PortfolioRefusal(4, "capital"), s.Latest.Refusal);
        Assert.Equal((3_000m, 4), (s.Latest.Owner!.Capital, s.Latest.Owner.AppliedSequence));
        Assert.Equal((3_000m, 4), (s.Owner!.Capital, s.Owner.AppliedSequence)); // the next run looks from portfolio-5 on
    }

    [Fact]
    public async Task Run_ASubmissionThatIsNotOneIsRefused()
    {
        var s = new Setup();
        s.Store.SubmitJson(1, """{"sequence":"1","capital":"oops"}""");

        await s.RunAsync();

        Assert.Equal(new PortfolioRefusal(1, "unreadable"), s.Latest.Refusal);
        Assert.Null(s.Latest.Owner);

        // One whose number isn't its own.
        s.NextDay();
        s.Store.SubmitJson(2, System.Text.Json.JsonSerializer.Serialize(Submission(7, 5_000m), StocksJson.Options));
        await s.RunAsync();
        Assert.Equal(new PortfolioRefusal(2, "sequence"), s.Latest.Refusal);
    }

    [Fact]
    public async Task Run_ASplitMovesTheOwnersHoldingIntoTheNewTerms()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m, Held("XYZ.US", 10m, 120m, stop: 130m)));
        await s.RunAsync();

        // Two for one: every bar halves.
        s.NextDay();
        s.Source.Factor["XYZ.US"] = 0.5m;
        await s.RunAsync();

        var holding = Assert.Single(s.Latest.Owner!.Holdings);
        Assert.Equal((20m, 60m), (holding.Quantity, holding.AverageCost));
        Assert.Contains(new NoteLine("XYZ.US", HostReasons.Adjusted), s.Latest.Owner.Notes);
        // The stop, raised to 139.2 the night before, halves with it.
        Assert.Equal(69.6m, holding.Stop);
    }

    [Fact]
    public async Task Run_AFetchThatFailsOnTheOwnersHoldingIsNotedInItsSection()
    {
        var s = new Setup();
        s.Source.Failing.Add("OWN.US");
        s.Store.Submit(Submission(1, 5_000m, Held("OWN.US", 5m, 100m)));

        await s.RunAsync();

        var owner = s.Latest.Owner!;
        Assert.Contains(new NoteLine("OWN.US", HostReasons.DataError), owner.Notes);
        Assert.Null(owner.Value); // no close to value it at
        Assert.DoesNotContain(s.Latest.Notes, n => n.Symbol == "OWN.US");
    }

    [Theory]
    [InlineData("SAP.XETRA", "symbol")] // not a US share
    [InlineData("xyz.us", "symbol")]
    [InlineData("XYZ.US", "figures", "0")]
    [InlineData("XYZ.US", "figures", "1", "-1")]
    [InlineData("XYZ.US", "opened", "1", "1", "2026-10-29")]
    public void Apply_RefusesWhatTheBrokerCouldNotHold(string symbol, string error, string quantity = "1", string cost = "1", string? opened = null)
    {
        var holding = new OwnerHoldingInput(
            symbol, null, decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture),
            null, opened is null ? null : DateOnly.Parse(opened, System.Globalization.CultureInfo.InvariantCulture));

        var (state, refused) = OwnerPortfolio.Apply(Submission(1, 1_000m, holding));

        Assert.Null(state);
        Assert.Equal(error, refused);
    }

    [Fact]
    public void Apply_RefusesTwiceTheSameSymbolAndCapitalOutOfBounds()
    {
        Assert.Equal("symbol", OwnerPortfolio.Apply(Submission(1, 1_000m, Held("XYZ.US", 1m, 1m), Held("XYZ.US", 2m, 1m))).Error);
        Assert.Equal("capital", OwnerPortfolio.Apply(Submission(1, 2_000_000m)).Error);
        Assert.Equal("holdings", OwnerPortfolio.Apply(Submission(1, 1_000m, Enumerable.Range(0, 51).Select(i => Held($"S{i}.US", 1m, 1m)).ToArray())).Error);
        Assert.Equal("sequence", OwnerPortfolio.Apply(Submission(0, 1_000m)).Error);
    }

    [Fact]
    public void Apply_KeepsTheFiguresAndDropsANameThatCouldDisguiseTheTicker()
    {
        var (state, error) = OwnerPortfolio.Apply(Submission(
            3, 1_000m, Held("XYZ.US", 2.5m, 10m, stop: 9m, name: " XYZ Corp "), new OwnerHoldingInput("ABC.US", "ABC‮", 1m, 1m, null, null)));

        Assert.Null(error);
        Assert.Equal((1_000m, 3, 26m), (state!.Capital, state.AppliedSequence, state.Invested));
        Assert.Equal(new OwnerHolding("XYZ.US", "XYZ Corp", 2.5m, 10m, 9m, new DateOnly(2026, 9, 1)), state.Holdings[0]);
        // No opening day: the submission's.
        Assert.Equal(new OwnerHolding("ABC.US", null, 1m, 1m, null, new DateOnly(2026, 10, 28)), state.Holdings[1]);
    }
}
