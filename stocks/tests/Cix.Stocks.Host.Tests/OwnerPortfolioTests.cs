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

        public void NextDay(int days = 1)
        {
            Source.LastDay = Source.LastDay.AddDays(days);
            Time.Now = Time.Now.AddDays(days);
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
        Assert.Equal((3_000m, 3), (s.Latest.Owner!.Capital, s.Latest.Owner.AppliedSequence));
        Assert.Equal((3_000m, 3, 4), (s.Owner!.Capital, s.Owner.AppliedSequence, s.Owner.SeenSequence)); // the next run looks from portfolio-5 on

        // Sunday night: no new close, so nothing is advised, and the one sent waits for the next close.
        s.NextDay();
        s.Store.Submit(Submission(5, 2_000m));
        Assert.Equal(RunOutcome.AlreadyAdvised, (await s.RunAsync()).Outcome);
        Assert.Equal((3_000m, 4), (s.Latest.Owner!.Capital, s.Latest.Refusal!.Sequence));
        Assert.Equal(4, s.Owner!.SeenSequence);

        // Tuesday night, Monday's close: applied, and the refusal goes.
        s.NextDay(2);
        Assert.Equal(RunOutcome.Advised, (await s.RunAsync()).Outcome);
        Assert.Null(s.Latest.Refusal);
        Assert.Equal((2_000m, 5), (s.Latest.Owner!.Capital, s.Latest.Owner.AppliedSequence));
    }

    [Fact]
    public async Task Run_ARefusalBeforeAnyPortfolioIsRememberedAndTheNextOneApplies()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 0m));
        await s.RunAsync();
        Assert.Null(s.Latest.Owner);
        Assert.Equal(new PortfolioRefusal(1, "capital"), s.Latest.Refusal);
        Assert.Equal((0, 1), (s.Owner!.AppliedSequence, s.Owner.SeenSequence));

        // Not looked at again: the night after it is still the refusal, and no portfolio is advised.
        s.NextDay();
        await s.RunAsync();
        Assert.Null(s.Latest.Owner);
        Assert.Equal(new PortfolioRefusal(1, "capital"), s.Latest.Refusal);

        s.NextDay();
        s.Store.Submit(Submission(2, 5_000m));
        await s.RunAsync();
        Assert.Null(s.Latest.Refusal);
        Assert.Equal((5_000m, 2), (s.Latest.Owner!.Capital, s.Latest.Owner.AppliedSequence));
    }

    [Fact]
    public async Task Run_ASubmissionAsTheDashboardWritesItIsApplied()
    {
        // Byte for byte the shape dashboard/api/src/portfolio.js portfolioDocument writes: a numeric sequence, decimal
        // strings, nulls for what wasn't entered, and the page's clientId.
        var s = new Setup();
        s.Store.SubmitJson(1, """
            {"sequence":1,"submittedAt":"2026-10-28T18:00:00.000Z","capital":"5000","holdings":[
              {"symbol":"XYZ.US","name":null,"quantity":"10","averageCost":"120.5","stop":null,"openedOn":null},
              {"symbol":"OWN.US","name":"Own Inc","quantity":"2.5","averageCost":"100","stop":"90","openedOn":"2026-09-01"}],
             "clientId":"0b7c4bde-2f8e-4a52-9a43-5d1f2f6f3c11"}
            """);

        await s.RunAsync();

        Assert.Null(s.Latest.Refusal);
        var owner = s.Owner!;
        Assert.Equal((5_000m, 1, 1), (owner.Capital, owner.AppliedSequence, owner.SeenSequence));
        Assert.Equal(("XYZ.US", (string?)null, 10m, 120.5m, Wednesday), (owner.Holdings[0].Symbol, owner.Holdings[0].Name, owner.Holdings[0].Quantity, owner.Holdings[0].AverageCost, owner.Holdings[0].OpenedOn));
        Assert.Equal(("Own Inc", 2.5m, 100m, new DateOnly(2026, 9, 1)), (owner.Holdings[1].Name, owner.Holdings[1].Quantity, owner.Holdings[1].AverageCost, owner.Holdings[1].OpenedOn));
    }

    [Fact]
    public async Task Run_ATradeAsTheDashboardWritesItIsApplied()
    {
        // Byte for byte the shape dashboard/api/src/portfolio.js tradeValue writes: no capital or holdings, the trade's
        // figures as decimal strings, null fees and stop when not entered.
        var s = new Setup();
        s.Store.SubmitJson(1, """
            {"sequence":1,"submittedAt":"2026-10-28T18:00:00.000Z","capital":"5000","holdings":[
              {"symbol":"OWN.US","name":"Own Inc","quantity":"10","averageCost":"100","stop":"90","openedOn":"2026-09-01"}],
             "clientId":"0b7c4bde-2f8e-4a52-9a43-5d1f2f6f3c11"}
            """);
        s.Store.SubmitJson(2, """
            {"sequence":2,"submittedAt":"2026-10-28T18:05:00.000Z","trade":
              {"kind":"sell","symbol":"OWN.US","name":null,"quantity":"4","price":"130.25","fees":null,"day":"2026-10-28","stop":null},
             "clientId":"2d9c2f4e-1a2b-4c3d-8e9f-0a1b2c3d4e5f"}
            """);
        s.Store.SubmitJson(3, """
            {"sequence":3,"submittedAt":"2026-10-28T18:06:00.000Z","trade":
              {"kind":"buy","symbol":"XYZ.US","name":"Xyz","quantity":"2.5","price":"40","fees":"0.5","day":"2026-10-28","stop":"36"},
             "clientId":"3e0d3f5a-2b3c-4d5e-9f0a-1b2c3d4e5f60"}
            """);

        await s.RunAsync();

        Assert.Null(s.Latest.Refusal);
        var owner = s.Owner!;
        // 5000 + 4 × (130.25 − 100) = 5121; the buy keeps the capital and costs (2.5 × 40 + 0.5) / 2.5 = 40.2 a share.
        Assert.Equal((5_121m, 3), (owner.Capital, owner.AppliedSequence));
        // (The stops are the night's advice's, which raises them on the test's bars.)
        Assert.Equal([("OWN.US", 6m, 100m), ("XYZ.US", 2.5m, 40.2m)], owner.Holdings.Select(h => (h.Symbol, h.Quantity, h.AverageCost)));
        Assert.Equal([("sell", 0m, (decimal?)121m), ("buy", 0.5m, null)], owner.Trades!.Select(t => (t.Kind, t.FeesBase, t.ProfitBase)));
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

    [Theory]
    [InlineData(200)] // deeper than any reader allows
    [InlineData(0)]
    public async Task Run_ADocumentTooDeepOrTooLargeIsRefusedAndTheRunStillAdvises(int depth)
    {
        var s = new Setup();
        var value = depth > 0 ? new string('[', depth) + new string(']', depth) : $$"""{"sequence":1,"capital":"1","pad":"{{new string('x', 100_000)}}"}""";
        s.Store.SubmitJson(1, value);

        var report = await s.RunAsync();

        Assert.Equal(RunOutcome.Advised, report.Outcome);
        Assert.Equal(new PortfolioRefusal(1, "unreadable"), s.Latest.Refusal);
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
    public async Task Run_TradesSentAfterASplitMeetTheSharesInTheNewTerms()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m, Held("XYZ.US", 10m, 120m)));
        await s.RunAsync();

        // Two for one, and the owner trades in the new shares before the run: 10 bought at 50, then 15 sold at 70.
        s.NextDay();
        s.Source.Factor["XYZ.US"] = 0.5m;
        s.Store.Submit(Trade(2, "buy", "XYZ.US", 10m, 50m));
        s.Store.Submit(Trade(3, "sell", "XYZ.US", 15m, 70m));
        await s.RunAsync();

        // 20 at 60 after the split; (20 × 60 + 10 × 50) / 30 = 56.66666667; 15 × (70 − 56.66666667) = 200.00 to the cent.
        Assert.Null(s.Latest.Refusal);
        var owner = s.Owner!;
        var holding = Assert.Single(owner.Holdings);
        Assert.Equal((15m, 56.66666667m, 5_200m), (holding.Quantity, holding.AverageCost, owner.Capital));
        Assert.Equal(200m, s.Store.Read<TradeLine>("trades/owner-3")!.ProfitBase);
        Assert.Contains(new NoteLine("XYZ.US", HostReasons.Adjusted), s.Latest.Owner!.Notes);

        // The night after, nothing moves again.
        s.NextDay();
        await s.RunAsync();
        Assert.Equal((15m, 56.66666667m), (s.Owner!.Holdings[0].Quantity, s.Owner.Holdings[0].AverageCost));
    }

    [Fact]
    public async Task Run_MoreHoldingsThanTheCallBudgetTakeTurns()
    {
        // 18 calls a night: the benchmark and 17 of the 20 holdings; the night after, the 3 left out go first.
        var s = new Setup();
        var symbols = Enumerable.Range(0, 20).Select(i => $"H{i:00}.US").ToList();
        s.Store.Submit(Submission(1, 100_000m, symbols.Select(x => Held(x, 1m, 100m)).ToArray()));

        await s.RunAsync();
        Assert.Equal(17, symbols.Count(x => s.Store.Read<CachedBars>($"bars/{x}") is not null));
        Assert.Contains(new NoteLine("H19.US", HostReasons.CallBudget), s.Latest.Owner!.Notes);

        s.NextDay();
        await s.RunAsync();
        Assert.All(symbols, x => Assert.NotNull(s.Store.Read<CachedBars>($"bars/{x}")));
        Assert.DoesNotContain(new NoteLine("H19.US", HostReasons.CallBudget), s.Latest.Owner!.Notes);
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
    [InlineData("XYZ.US", "opened", "1", "1", "2026-10-30")] // the day after is allowed: the owner's date may be ahead of UTC
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
    public void Apply_KeepsTheFiguresAndCleansANameThatCouldDisguiseTheTicker()
    {
        var (state, error) = OwnerPortfolio.Apply(Submission(
            3, 1_000m, Held("XYZ.US", 2.5m, 10m, stop: 9m, name: " XYZ Corp "), new OwnerHoldingInput("ABC.US", "A\u202EB\u200BC\u2028", 1m, 1m, null, null),
            new OwnerHoldingInput("EMO.US", string.Concat(Enumerable.Repeat("\U0001F600", 85)), 1m, 1m, null, new DateOnly(2026, 10, 29)),
            new OwnerHoldingInput("ZWJ.US", "\u200D\u200E", 1m, 1m, null, null)));

        Assert.Null(error);
        Assert.Equal((1_000m, 3, 3), (state!.Capital, state.AppliedSequence, state.SeenSequence));
        Assert.Equal(new OwnerHolding("XYZ.US", "XYZ Corp", 2.5m, 10m, 9m, new DateOnly(2026, 9, 1)), state.Holdings[0]);
        // Bidi, zero-width and line separators out; no opening day: the submission's.
        Assert.Equal(new OwnerHolding("ABC.US", "ABC", 1m, 1m, null, new DateOnly(2026, 10, 28)), state.Holdings[1]);
        // 80 code points, as the page counts them (160 UTF-16 units here).
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 80)), state.Holdings[2].Name);
        Assert.Null(state.Holdings[3].Name); // nothing left
    }

    private static PortfolioSubmission Trade(
        int sequence, string kind, string symbol, decimal quantity, decimal price, decimal? fees = null, DateOnly? day = null, decimal? stop = null) =>
        new(sequence, Sent, 0m, null, new OwnerTradeInput(kind, symbol, null, quantity, price, fees, day ?? Wednesday, stop));

    private static OwnerState Portfolio(decimal capital = 5_000m) =>
        OwnerPortfolio.Apply(Submission(1, capital, Held("XYZ.US", 10m, 120m, stop: 110m), Held("OWN.US", 5m, 100m))).State!;

    [Fact]
    public void Trade_ABuyAddsTheSharesAtTheirPriceWithTheFeesInTheCost()
    {
        var (state, error, line) = OwnerPortfolio.Apply(Portfolio(), Trade(2, "buy", "XYZ.US", 5m, 150m, fees: 1.5m));

        Assert.Null(error);
        // (10 × 120 + 5 × 150 + 1.5) / 15 = 130.1; the stop and the day bought stay; the capital stays (the cash falls).
        Assert.Equal(new OwnerHolding("XYZ.US", null, 15m, 130.1m, 110m, new DateOnly(2026, 9, 1)), state!.Holdings[0]);
        Assert.Equal((5_000m, 2, 2), (state.Capital, state.AppliedSequence, state.SeenSequence));
        Assert.Equal(new TradeLine(Wednesday, "XYZ.US", "buy", 5m, 150m, 1.5m, null, OwnerPortfolio.TradeReason), line);
        Assert.Equal([line!], state.Trades!);

        // A new holding opens on the trade's day, with the stop placed with it; a new stop replaces a held one's.
        var (bought, _, _) = OwnerPortfolio.Apply(state, Trade(3, "buy", "NEW.US", 2m, 50m, stop: 45m));
        Assert.Equal(new OwnerHolding("NEW.US", null, 2m, 50m, 45m, Wednesday), bought!.Holdings[^1]);
        Assert.Equal(125m, OwnerPortfolio.Apply(bought, Trade(4, "buy", "XYZ.US", 1m, 130m, stop: 125m)).State!.Holdings[0].Stop);
        Assert.Equal(2, bought.Trades!.Count);
    }

    [Fact]
    public void Trade_ASellAddsItsProfitOrLossToTheCapital()
    {
        // 4 × (140 − 120) − 1 = 79.
        var (state, error, line) = OwnerPortfolio.Apply(Portfolio(), Trade(2, "sell", "XYZ.US", 4m, 140m, fees: 1m));
        Assert.Null(error);
        Assert.Equal((5_079m, 6m, 120m), (state!.Capital, state.Holdings[0].Quantity, state.Holdings[0].AverageCost));
        Assert.Equal(new TradeLine(Wednesday, "XYZ.US", "sell", 4m, 140m, 1m, 79m, OwnerPortfolio.TradeReason), line);

        // All of OWN at a loss: 5 × (90 − 100) = −50, and the holding goes.
        var (sold, _, loss) = OwnerPortfolio.Apply(state, Trade(3, "sell", "OWN.US", 5m, 90m));
        Assert.Equal((5_029m, -50m), (sold!.Capital, loss!.ProfitBase));
        Assert.Equal(["XYZ.US"], sold.Holdings.Select(h => h.Symbol));

        // A whole portfolio sent afterwards replaces the holdings and keeps the record of trades.
        var replaced = OwnerPortfolio.Apply(sold, Submission(4, 6_000m)).State!;
        Assert.Empty(replaced.Holdings);
        Assert.Equal(2, replaced.Trades!.Count);
    }

    [Theory]
    [InlineData("short", "XYZ.US", "1", "1", null, 0, "kind")]
    [InlineData("sell", "ABC.US", "1", "1", null, 0, "not-held")]
    [InlineData("sell", "XYZ.US", "11", "1", null, 0, "too-many")]
    [InlineData("buy", "SAP.XETRA", "1", "1", null, 0, "symbol")]
    [InlineData("buy", "XYZ.US", "0", "1", null, 0, "figures")]
    [InlineData("buy", "XYZ.US", "1", "0", null, 0, "figures")]
    [InlineData("buy", "XYZ.US", "1", "1", "-1", 0, "figures")]
    [InlineData("buy", "XYZ.US", "1", "1", null, 2, "opened")] // the day after the submission's is allowed: the owner's date may be ahead
    public void Trade_RefusesWhatTheBrokerCouldNotHaveDone(string kind, string symbol, string quantity, string price, string? fees, int daysAfter, string error)
    {
        static decimal D(string v) => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        var trade = Trade(2, kind, symbol, D(quantity), D(price), fees is null ? null : D(fees), Wednesday.AddDays(daysAfter));

        Assert.Equal((null, error, null), OwnerPortfolio.Apply(Portfolio(), trade));
    }

    [Fact]
    public void Trade_NeedsAPortfolioAndCanNotLeaveTheCapitalAtNothing()
    {
        Assert.Equal("no-portfolio", OwnerPortfolio.Apply(null, Trade(1, "buy", "XYZ.US", 1m, 1m)).Error);
        // 5 × (0.01 − 100) = −499.95 off a capital of 100.
        Assert.Equal("capital", OwnerPortfolio.Apply(Portfolio(100m), Trade(2, "sell", "OWN.US", 5m, 0.01m)).Error);
        Assert.Equal("trade", OwnerPortfolio.Apply(Trade(2, "buy", "XYZ.US", 1m, 1m)).Error); // not a whole portfolio
        Assert.Equal("holdings", OwnerPortfolio.Apply(
            OwnerPortfolio.Apply(Submission(1, 1_000m, Enumerable.Range(0, 50).Select(i => Held($"S{i}.US", 1m, 1m)).ToArray())).State, Trade(2, "buy", "XYZ.US", 1m, 1m)).Error);
    }

    [Fact]
    public void Trade_AnAverageCostAboveThePriceCapOrADateAtTheCalendarsEndIsRefusedNotThrown()
    {
        // Fees far above a tiny buy: (0.00000001 × 1 + 1 000 000) / 0.00000001 a share, more than any price the dialog takes.
        Assert.Equal("figures", OwnerPortfolio.Apply(Portfolio(), Trade(2, "buy", "ABC.US", 0.00000001m, 1m, fees: 1_000_000m)).Error);
        // Added to a holding of 0.5 shares at 1: (0.5 + 0.00000001 + 1 000 000) / 0.50000001 ≈ 2 000 001.
        var small = OwnerPortfolio.Apply(Submission(1, 1_000m, Held("XYZ.US", 0.5m, 1m))).State;
        Assert.Equal("figures", OwnerPortfolio.Apply(small, Trade(2, "buy", "XYZ.US", 0.00000001m, 1m, fees: 1_000_000m)).Error);

        // A document written outside the dashboard, sent at the calendar's end: refused or applied, never an exception that
        // would stop every nightly run after it.
        var late = Trade(2, "buy", "XYZ.US", 1m, 1m, day: DateOnly.MaxValue) with { SubmittedAt = DateTimeOffset.MaxValue };
        Assert.Null(OwnerPortfolio.Apply(Portfolio(), late).Error);
        var portfolio = Submission(1, 1_000m, Held("XYZ.US", 1m, 1m, opened: DateOnly.MaxValue)) with { SubmittedAt = DateTimeOffset.MaxValue };
        Assert.Null(OwnerPortfolio.Apply(portfolio).Error);
    }

    [Fact]
    public async Task Run_TradesAreAppliedInOrderRecordedOnceAndShownNewestFirst()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m, Held("XYZ.US", 10m, 120m)));
        s.Store.Submit(Trade(2, "sell", "XYZ.US", 4m, 140m));
        s.Store.Submit(Trade(3, "sell", "ABC.US", 1m, 50m)); // not held: refused, and the next still applies
        s.Store.Submit(Trade(4, "buy", "XYZ.US", 2m, 130m));

        await s.RunAsync();

        var owner = s.Latest.Owner!;
        // 5000 + 4 × 20; XYZ (6 × 120 + 2 × 130) / 8 = 122.5, so 980 invested.
        Assert.Equal((5_080m, 980m, 4_100m, 4), (owner.Capital, owner.Invested, owner.Cash, owner.AppliedSequence));
        Assert.Equal((8m, 122.5m), (owner.Holdings[0].Quantity, owner.Holdings[0].AverageCost));
        Assert.Equal(new PortfolioRefusal(3, "not-held", "sell", "ABC.US"), s.Latest.Refusal); // which trade, for the page
        Assert.Equal([("buy", 2m), ("sell", 4m)], owner.Trades!.Select(t => (t.Kind, t.Quantity)));
        Assert.Equal(80m, s.Store.Read<TradeLine>("trades/owner-2")!.ProfitBase);
        Assert.NotNull(s.Store.Read<TradeLine>("trades/owner-4"));
        Assert.Null(s.Store.Read<TradeLine>("trades/owner-3"));
        Assert.Equal(4, s.Owner!.SeenSequence);

        // The next night: nothing new, the record stays, and the refusal goes once a later change applies.
        s.NextDay();
        await s.RunAsync();
        Assert.Equal(2, s.Latest.Owner!.Trades!.Count);
        s.NextDay();
        s.Store.Submit(Trade(5, "sell", "XYZ.US", 8m, 125m));
        await s.RunAsync();
        Assert.Null(s.Latest.Refusal);
        Assert.Empty(s.Latest.Owner!.Holdings);
        Assert.Equal(5_100m, s.Latest.Owner.Capital); // + 8 × 2.5
    }

    [Fact]
    public async Task Run_TradesAfterARefusedPortfolioAreRefusedAndTheCauseIsShown()
    {
        var s = new Setup();
        s.Store.Submit(Submission(1, 5_000m, Held("XYZ.US", 10m, 120m)));
        s.Store.Submit(Submission(2, 0m, Held("XYZ.US", 20m, 120m))); // refused: no capital
        s.Store.Submit(Trade(3, "sell", "XYZ.US", 4m, 140m)); // made on the refused portfolio: not applied to the old one
        s.Store.Submit(Submission(4, 6_000m, Held("XYZ.US", 16m, 120m)));
        s.Store.Submit(Trade(5, "sell", "XYZ.US", 6m, 130m)); // after a portfolio that applied: applies

        await s.RunAsync();

        Assert.Equal(new PortfolioRefusal(2, "capital"), s.Latest.Refusal);
        var owner = s.Owner!;
        Assert.Equal((6_060m, 10m, 5, 5), (owner.Capital, owner.Holdings[0].Quantity, owner.AppliedSequence, owner.SeenSequence));
        Assert.Null(s.Store.Read<TradeLine>("trades/owner-3"));
        Assert.Equal(60m, s.Store.Read<TradeLine>("trades/owner-5")!.ProfitBase);
    }
}
