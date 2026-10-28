using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.Pairs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

public sealed class TradablePairsTests
{
    private const string Btc = Rig.Btc;
    private const string Sol = "SOL-EUR";

    /// <summary>Any EUR pair may be watched, three at most.</summary>
    private static RiskLimits OpenLimits { get; } = Rig.Limits with { MaxTradableSymbols = 3 };

    [Fact]
    public async Task Add_ActiveLiquidPair_JournalsSavesAndMakesItTradable()
    {
        var setup = new Setup();
        setup.Request("r-1", "add", Sol, note: "more choice");

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.True(setup.Tradable.Contains(Sol));
        Assert.Equal(Rig.T0, setup.Tradable.AddedAt(Sol));
        Assert.Equal("accepted", setup.RequestStatus("r-1"));
        Assert.Equal(new[] { Btc, Sol }, setup.Repository.Saved[TradingMode.Paper].Symbols);
        var entry = Assert.Single(setup.Changes);
        Assert.Equal("added", entry.Data!["action"]);
        Assert.Equal("BTC-EUR,SOL-EUR", entry.Data["to"]);
        Assert.Equal("more choice", entry.Data["note"]);
        var catalog = setup.Repository.Catalog!;
        Assert.Equal(new[] { Btc, Sol }, catalog.Tradable);
        Assert.Contains(catalog.Pairs, p => p.Symbol == Sol && p.Tradable && p.Permitted);
        Assert.DoesNotContain(catalog.Pairs, p => p.Symbol == "ETH-USD" || p.Symbol == "DOGE-EUR"); // another quote; inactive
    }

    [Theory]
    [InlineData("ETH-USD", "Only EUR pairs are permitted by config/risk-limits.json.")]
    [InlineData("DOGE-EUR", "doesn't list it as an active pair")]
    [InlineData("ZZZ-EUR", "doesn't list it as an active pair")]
    public async Task Add_PairTheChecksRefuse_ChangesNothing(string symbol, string why)
    {
        var setup = new Setup();
        setup.Request("r-1", "add", symbol);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { Btc }, setup.Tradable.Current);
        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Contains(why, setup.RequestDetail("r-1"), StringComparison.Ordinal);
        Assert.Equal("refused", Assert.Single(setup.Changes).Data!["action"]);
        Assert.Empty(setup.Repository.Saved);
    }

    [Fact]
    public async Task Add_WhenTheCapIsReached_IsRefused()
    {
        var setup = new Setup();
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol, "ADA-EUR"], Rig.T0));
        setup.Request("r-1", "add", "LINK-EUR");

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Contains("At most 3 pairs", setup.RequestDetail("r-1"), StringComparison.Ordinal);
        Assert.False(setup.Tradable.Contains("LINK-EUR"));
    }

    [Fact]
    public async Task Add_AThinPair_IsWatched_AndTheStrategyDecidesWhetherItTrades()
    {
        var setup = new Setup();
        setup.Request("r-1", "add", "XRP-EUR"); // 24 EUR traded in a day: no check at add time (ADR-0021)

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("accepted", setup.RequestStatus("r-1"));
        Assert.True(setup.Tradable.Contains("XRP-EUR"));
        Assert.Equal(0, setup.Market.CandleRequests); // the add asks the exchange for its pairs only
    }

    [Fact]
    public async Task Live_ChangesItsOwnList_FromLiveRequestsOnly()
    {
        var setup = new Setup(mode: TradingMode.Live);
        setup.Request("r-paper", "add", "ADA-EUR", mode: TradingMode.Paper);
        setup.Request("r-live", "add", Sol, minute: 1);
        setup.Rig.Time.Advance(TimeSpan.FromMinutes(2));

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { Btc, Sol }, setup.Tradable.Current.Order(StringComparer.Ordinal));
        Assert.Equal(("accepted", "new"), (setup.RequestStatus("r-live"), setup.RequestStatus("r-paper")));
        Assert.Equal(new[] { Btc, Sol }, setup.Repository.Saved[TradingMode.Live].Symbols);
        Assert.False(setup.Repository.Saved.ContainsKey(TradingMode.Paper));
        Assert.Equal("live", Assert.Single(setup.Changes).Data!["mode"]);
    }

    [Fact]
    public async Task Live_NamesItsOwnLimitsFile_WhenAPairIsntPermitted()
    {
        var setup = new Setup(mode: TradingMode.Live);
        setup.Request("r-1", "add", "ETH-USD");

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Equal("Only EUR pairs are permitted by config/risk-limits.live.json.", setup.RequestDetail("r-1"));
    }

    [Fact]
    public async Task Remove_HeldCoin_IsRefused()
    {
        var setup = new Setup(btc: 0.001m);
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], Rig.T0));
        setup.Request("r-1", "remove", Btc);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Contains("You still hold it", setup.RequestDetail("r-1"), StringComparison.Ordinal);
        Assert.True(setup.Tradable.Contains(Btc));
    }

    [Fact]
    public async Task Remove_AResidualTooSmallToSell_IsRefusedUntilTheBalanceIsZero()
    {
        var setup = new Setup(btc: 0.000001m); // about 0.06 EUR: under the exchange's 0.1 EUR minimum, so not sellable
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], Rig.T0));
        setup.Request("r-1", "remove", Btc);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Contains("only when its balance is zero", setup.RequestDetail("r-1"), StringComparison.Ordinal);
        Assert.True(setup.Tradable.Contains(Btc));
    }

    [Fact]
    public async Task Remove_CoinNotHeld_StopsItsOrdersAtTheGate()
    {
        var setup = new Setup();
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], Rig.T0));
        setup.Request("r-1", "remove", Btc);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("accepted", setup.RequestStatus("r-1"));
        Assert.Equal(new[] { Sol }, setup.Tradable.Current);
        Assert.Equal(new[] { Sol }, setup.Repository.Saved[TradingMode.Paper].Symbols);
        var result = await setup.Rig.Buy(0.0005m, 60_010m);
        Assert.Equal(ExecutionStatus.Rejected, result.Status);
        Assert.Equal("symbol-allowlist", result.Rule);
    }

    [Fact]
    public async Task Remove_CoinWithAnOrderWorking_IsRefused()
    {
        var setup = new Setup();
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], Rig.T0));
        Assert.Equal(ExecutionStatus.Placed, (await setup.Rig.Buy(0.0005m, 59_950m)).Status); // rests below the ask
        setup.Request("r-1", "remove", Btc);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Rejected, setup.RequestStatus("r-1"));
        Assert.Contains("order", setup.RequestDetail("r-1"), StringComparison.Ordinal);
        Assert.True(setup.Tradable.Contains(Btc));
    }

    [Fact]
    public async Task Remove_LastPair_LeavesNothingWatched()
    {
        var setup = new Setup();
        setup.Request("r-1", "remove", Btc);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("accepted", setup.RequestStatus("r-1"));
        Assert.Empty(setup.Tradable.Current);
        Assert.Empty(setup.Repository.Saved[TradingMode.Paper].Symbols);
    }

    [Fact]
    public async Task Requests_StaleOrMalformed_AreClosedWithoutAChange()
    {
        var setup = new Setup();
        setup.Request("r-old", "add", Sol, minute: -6);
        setup.Request("r-verb", "swap", Sol);
        setup.Request("r-symbol", "add", "sol-eur");

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(ManualRequestStatus.Expired, setup.RequestStatus("r-old"));
        Assert.Equal(ManualRequestStatus.Invalid, setup.RequestStatus("r-verb"));
        Assert.Equal(ManualRequestStatus.Invalid, setup.RequestStatus("r-symbol"));
        Assert.Equal(new[] { Btc }, setup.Tradable.Current);
        Assert.Empty(setup.Changes);
    }

    [Fact]
    public async Task Poll_WithoutTheLease_DoesNothing()
    {
        var setup = new Setup(lease: new FixedLease(held: false));
        setup.Request("r-1", "add", Sol);

        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("new", setup.RequestStatus("r-1"));
        Assert.False(setup.Tradable.Contains(Sol));
    }

    [Fact]
    public async Task Initialize_RestoresTheSavedList_DroppingPairsNoLongerPermitted()
    {
        var setup = new Setup();
        setup.Repository.Saved[TradingMode.Paper] = new TradablePairsDocument([Btc, Sol, "ETH-USD"], Rig.T0.AddDays(-1), "owner");

        await setup.Service.InitializeAsync(CancellationToken.None);

        Assert.Equal(new[] { Btc, Sol }, setup.Tradable.Current.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { Btc, Sol }, setup.Repository.Saved[TradingMode.Paper].Symbols); // saved as it now is
        Assert.NotNull(setup.Repository.Catalog);

        // ETH-USD may still be held: trading halts until the owner has looked.
        await setup.Service.PollOnceAsync(CancellationToken.None);
        Assert.True(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal(TradablePairsService.By, setup.Rig.KillSwitch.State.By);
    }

    [Fact]
    public async Task Initialize_SavedListAsSaved_KeepsTrading()
    {
        var setup = new Setup();
        setup.Repository.Saved[TradingMode.Paper] = new TradablePairsDocument([Btc, Sol], Rig.T0.AddDays(-1), "owner");

        await setup.Service.InitializeAsync(CancellationToken.None);
        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task Initialize_NothingSaved_WatchesNothing_UntilTheOwnerAddsACoin()
    {
        var setup = new Setup(watched: []);

        await setup.Service.InitializeAsync(CancellationToken.None);
        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(setup.Tradable.Current); // no list from configuration (ADR-0021)
        Assert.Empty(setup.Changes);
        Assert.Empty(setup.Repository.Catalog!.Tradable);
        Assert.Equal(("EUR", 3), (setup.Repository.Catalog.QuoteCurrency, setup.Repository.Catalog.Cap));
        Assert.False(setup.Rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task Initialize_AddsTheCoinsTheAccountHolds_JournaledBeforeTheyAreWatched()
    {
        var setup = new Setup(btc: 0.001m, watched: []);
        setup.Repository.Saved[TradingMode.Paper] = new TradablePairsDocument([Sol], Rig.T0.AddDays(-1), "owner");

        await setup.Service.InitializeAsync(CancellationToken.None);

        Assert.Equal(new[] { Btc, Sol }, setup.Tradable.Current.Order(StringComparer.Ordinal)); // BTC is held: never outside equity
        Assert.Equal(new[] { Btc, Sol }, setup.Repository.Saved[TradingMode.Paper].Symbols);
        var adopted = Assert.Single(setup.Changes);
        Assert.Equal((TradablePairsService.AdoptedAction, Btc, "engine", "SOL-EUR", "BTC-EUR,SOL-EUR"),
            (adopted.Data!["action"], adopted.Data["symbol"], adopted.Data["requestedBy"], adopted.Data["from"], adopted.Data["to"]));
        Assert.True((await setup.Rig.Execution.SampleAsync(CancellationToken.None))!.PositionQuoteBySymbol[Btc] > 0m);
    }

    [Fact]
    public async Task Initialize_WhenTheBalancesCantBeRead_Throws_SoStartupRetries()
    {
        var setup = new Setup(btc: 0.001m, watched: []);
        setup.Rig.Exchange.FailBalances = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.InitializeAsync(CancellationToken.None));

        setup.Rig.Exchange.FailBalances = false;
        await setup.Service.InitializeAsync(CancellationToken.None);
        Assert.True(setup.Tradable.Contains(Btc));
    }

    [Fact]
    public async Task Poll_AddsADeposit_AndSaysOnceWhyACoinItCantWatchIsLeftOut()
    {
        var setup = new Setup();
        await setup.Service.InitializeAsync(CancellationToken.None);
        setup.Rig.Exchange.ExtraBalances["SOL"] = 2m; // a deposit made outside the engine
        setup.Rig.Exchange.ExtraBalances["DOGE"] = 100m; // its pair is suspended: nothing to price or sell it with
        setup.Rig.Exchange.ExtraBalances["ETH"] = 0m; // nothing held

        await setup.Service.PollOnceAsync(CancellationToken.None);
        await setup.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { Btc, Sol }, setup.Tradable.Current.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { Btc, Sol }, setup.Repository.Catalog!.Tradable);
        var notAdopted = Assert.Single(setup.Changes, e => e.Data!["action"] == "not-adopted"); // not every poll
        Assert.Equal("DOGE-EUR", notAdopted.Data!["symbol"]);
        Assert.Single(setup.Changes, e => e.Data!["action"] == TradablePairsService.AdoptedAction);

        // With the cap reached, a held coin can't be added either: the journal says why, once.
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol, "ADA-EUR"], Rig.T0));
        setup.Rig.Exchange.ExtraBalances["LINK"] = 1m;
        await setup.Service.PollOnceAsync(CancellationToken.None);
        Assert.Contains("At most 3 pairs", Assert.Single(setup.Changes, e => e.Data!["symbol"] == "LINK-EUR").Data!["problem"], StringComparison.Ordinal);
    }

    [Fact]
    public void RiskLimits_TheRepositorysFiles_FitTheRule()
    {
        // Both owner-only files as deployed: a quote currency and a cap, no pair list (ADR-0021). A stale file fails here,
        // before an engine could be deployed with it.
        foreach (var mode in new[] { TradingMode.Paper, TradingMode.Live })
        {
            var limits = RiskLimitsOptions.LoadFor(mode, AppContext.BaseDirectory);
            Assert.Empty(limits.Validate());
            Assert.Equal("EUR", limits.AllowedQuoteCurrency);
            Assert.InRange(limits.MaxTradableSymbols, 1, RiskLimits.MaxTradableSymbolsLimit);
        }
    }

    [Fact]
    public void RiskLimits_ComeOnlyFromTheOwnersFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"risk-limits-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "Risk": { "AllocationQuote": 200, "MaxOrderQuote": 40, "MaxPositionQuotePerSymbol": 100, "MaxDailyLossQuote": 15,
              "MaxDrawdownFraction": 0.15, "MaxOrdersPerMinute": 4, "MaxPriceDeviationFraction": 0.01, "AllowedQuoteCurrency": "EUR", "MaxTradableSymbols": 5 } }
            """);
        try
        {
            var environment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Risk:AllowedQuoteCurrency"] = "USD", ["Risk:MaxTradableSymbols"] = "50" })
                .Build();

            Assert.Throws<InvalidOperationException>(() => RiskLimitsOptions.Load(path, environment));
            var limits = RiskLimitsOptions.Load(path, new ConfigurationBuilder().Build());
            Assert.Equal(("EUR", 5), (limits.AllowedQuoteCurrency, limits.MaxTradableSymbols));
            Assert.False(limits.Permits("ETH-USD"));

            // A file that still lists pairs stops startup and names the key: no pair list lives in configuration.
            File.WriteAllText(path, """
                { "Risk": { "AllocationQuote": 200, "MaxOrderQuote": 40, "MaxPositionQuotePerSymbol": 100, "MaxDailyLossQuote": 15,
                  "MaxDrawdownFraction": 0.15, "MaxOrdersPerMinute": 4, "MaxPriceDeviationFraction": 0.01, "AllowedQuoteCurrency": "EUR",
                  "MaxTradableSymbols": 5, "AllowedSymbols": [ "BTC-EUR" ] } }
                """);
            var stale = Assert.Throws<InvalidOperationException>(() => RiskLimitsOptions.Load(path, new ConfigurationBuilder().Build()));
            Assert.Contains("Risk:AllowedSymbols", stale.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RiskLimits_EachModeHasItsOwnFile_AndLiveNeverFallsBackToPapers()
    {
        static string Limits(int allocation) => $$"""
            { "Risk": { "AllocationQuote": {{allocation}}, "MaxOrderQuote": 20, "MaxPositionQuotePerSymbol": 50, "MaxDailyLossQuote": 5,
              "MaxDrawdownFraction": 0.15, "MaxOrdersPerMinute": 4, "MaxPriceDeviationFraction": 0.01, "AllowedQuoteCurrency": "EUR", "MaxTradableSymbols": 5 } }
            """;
        var dir = Directory.CreateTempSubdirectory("cix-limits-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "risk-limits.json"), Limits(200));

            Assert.Equal(200m, RiskLimitsOptions.LoadFor(TradingMode.Paper, dir).AllocationQuote);
            var missing = Assert.Throws<InvalidOperationException>(() => RiskLimitsOptions.LoadFor(TradingMode.Live, dir));
            Assert.Contains("risk-limits.live.json", missing.Message, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, "risk-limits.live.json"), Limits(100));

            Assert.Equal(100m, RiskLimitsOptions.LoadFor(TradingMode.Live, dir).AllocationQuote);
            Assert.Equal(200m, RiskLimitsOptions.LoadFor(TradingMode.Paper, dir).AllocationQuote);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyMonitor_NewPair_GetsTimeForItsFirstQuotes()
    {
        var setup = new Setup();
        setup.Rig.Time.Advance(TimeSpan.FromMinutes(20)); // past the start grace
        setup.Rig.Quote(60_000m, 60_010m);
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], setup.Rig.Time.GetUtcNow()));

        Assert.Null((await setup.Rig.Monitor.CheckOnceAsync(CancellationToken.None)).KillSwitchReason);

        // A quiet coin nobody holds no longer halts the others (ADR-0021): the gate and the scan leave it out by themselves.
        setup.Rig.Time.Advance(TimeSpan.FromMinutes(16));
        setup.Rig.Quote(60_000m, 60_010m);
        Assert.Null((await setup.Rig.Monitor.CheckOnceAsync(CancellationToken.None)).KillSwitchReason);
        Assert.False(setup.Rig.KillSwitch.IsEngaged);

        // Every watched coin quiet: the feed is down, and that still halts.
        setup.Rig.Time.Advance(TimeSpan.FromMinutes(16));
        var reason = (await setup.Rig.Monitor.CheckOnceAsync(CancellationToken.None)).KillSwitchReason;
        Assert.Equal("No fresh market data for any watched coin for over 15 minutes.", reason);
    }

    [Fact]
    public async Task SafetyMonitor_AHeldCoinWithoutFreshData_StillHalts()
    {
        var setup = new Setup(btc: 0.001m);
        Assert.Empty(setup.Tradable.TrySet([Btc, Sol], Rig.T0));
        setup.Rig.Time.Advance(TimeSpan.FromMinutes(16));
        setup.Rig.State.Update(new MarketQuote(Sol, 150m, 1m, 150.1m, 1m, setup.Rig.Time.GetUtcNow())); // SOL fresh; held BTC not

        var reason = (await setup.Rig.Monitor.CheckOnceAsync(CancellationToken.None)).KillSwitchReason;

        Assert.Equal("No fresh market data for BTC-EUR for over 15 minutes.", reason); // its stop can't be watched
        Assert.True(setup.Rig.KillSwitch.IsEngaged);
    }

    private sealed class Setup
    {
        /// <param name="watched">The list before the test starts; BTC-EUR alone when not given.</param>
        public Setup(RiskLimits? limits = null, decimal btc = 0m, FixedLease? lease = null, TradingMode mode = TradingMode.Paper, string[]? watched = null)
        {
            Mode = mode;
            Tradable = new TradableSymbols(limits ?? OpenLimits, watched ?? [Btc]);
            Rig = new Rig(btc: btc, pairSource: Market, journal: Journal, tradable: Tradable);
            Service = new TradablePairsService(
                Tradable, Repository, Rig.Pairs, Market, Rig.Execution, Journal, new TradingContext(mode), Rig.Time,
                NullLogger<TradablePairsService>.Instance, Requests, lease: lease);
        }

        public TradingMode Mode { get; }
        public PairMarket Market { get; } = new();
        public InMemoryJournal Journal { get; } = new();
        public InMemoryManualRequestStore Requests { get; } = new("ok");
        public InMemoryTradablePairsRepository Repository { get; } = new();
        public TradableSymbols Tradable { get; }
        public Rig Rig { get; }
        public TradablePairsService Service { get; }

        public IReadOnlyList<JournalEntry> Changes => Journal.Entries.Where(e => e.Kind == TradablePairsService.Kind).ToList();

        /// <summary>A pair request as the dashboard creates it, <paramref name="minute"/> minutes after T0.</summary>
        public void Request(string id, string action, string symbol, int minute = 0, string? note = null, TradingMode? mode = null)
        {
            var at = Rig.T0.AddMinutes(minute);
            Requests.Add(new JsonObject
            {
                ["id"] = id, ["day"] = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["createdAt"] = at.ToString("O", CultureInfo.InvariantCulture),
                ["type"] = TradablePairsService.RequestType, ["action"] = action, ["symbol"] = symbol, ["note"] = note,
                ["requestedBy"] = "owner", ["tradingMode"] = (mode ?? Mode).ToString().ToLowerInvariant(), ["status"] = "new",
            });
        }

        public string? RequestStatus(string id) => Requests.Find(id)?.Document["status"]?.GetValue<string>();

        public string RequestDetail(string id) =>
            Requests.Find(id)?.Document["events"] is JsonArray events ? events[^1]?["detail"]?.GetValue<string>() ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// The exchange's pairs: BTC, SOL, ADA, LINK and XRP active in EUR, DOGE inactive, ETH in USD. SOL, ADA and LINK traded
    /// 24 × 100 × 100 = 240,000 EUR over the last day; XRP 24 × 1 × 1 = 24 EUR.
    /// </summary>
    internal sealed class PairMarket : IMarketDataSource
    {
        private static PairInfo Pair(string symbol, bool active = true) =>
            new(symbol, symbol.Split('-')[0], symbol.Split('-')[1], 0.00000001m, 0.01m, 0.00000001m, 200m, 0.1m, active);

        public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PairInfo>>(
                [Pair(Btc), Pair(Sol), Pair("ADA-EUR"), Pair("LINK-EUR"), Pair("XRP-EUR"), Pair("DOGE-EUR", active: false), Pair("ETH-USD")]);

        /// <summary>How many candle requests were made: adding a pair makes none (ADR-0021).</summary>
        public int CandleRequests { get; private set; }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
        {
            CandleRequests++;
            var size = symbol == "XRP-EUR" ? 1m : 100m;
            // The 24 hourly candles closed by T0: 12:00 the day before up to 11:00.
            var candles = Enumerable.Range(1, 24)
                .Select(h => new Candle(Rig.T0.AddHours(-h), size, size, size, size, size))
                .Where(c => c.Start >= since)
                .ToList();
            return Task.FromResult<IReadOnlyList<Candle>>(candles);
        }
    }
}
