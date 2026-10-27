using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Engine.Execution;
using Cix.Engine.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The status record the dashboard reads: heartbeat, last sync, portfolio with plans, and database health.</summary>
public sealed class EngineStatusTests
{
    private static EngineStatusService Service(Rig rig, IEngineStatusStore store, DependencyHealth health) =>
        new(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), store, health, rig.Time,
            NullLogger<EngineStatusService>.Instance);

    [Fact]
    public async Task Status_ShowsThePortfolioWithPlans_AndTheLastSync_WithoutCallingTheExchange()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 60_010m, "c-1"), postOnly: false, CancellationToken.None,
            context: new OrderContext("place_order", 59_000m, 62_000m));
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        await rig.Execution.SampleAsync(CancellationToken.None); // what the safety monitor does every minute
        var store = new InMemoryEngineStatusStore();
        var lookups = rig.Exchange.OpenOrderLookups;

        Assert.True(await Service(rig, store, new DependencyHealth()).WriteOnceAsync(CancellationToken.None));

        Assert.Equal(lookups, rig.Exchange.OpenOrderLookups); // built from memory
        var status = store.Last!;
        Assert.Equal(("paper", false, Rig.T0, Rig.T0), (status.Mode, status.Halted, status.LastReconciledAt, status.OldestQuoteAt));
        Assert.Null(status.LastReconcileProblem);
        Assert.Empty(status.UnquotedSymbols);
        var portfolio = status.Portfolio!;
        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(("BTC-EUR", 30.00m, 59_000m, 62_000m, PositionPlanState.Open), (holding.Symbol, holding.ValueQuote, holding.StopLoss, holding.TakeProfit, holding.PlanStatus));
        Assert.Equal("EUR", portfolio.Currency);
        Assert.Equal(portfolio.Equity - 30.00m, portfolio.Cash);
    }

    [Fact]
    public async Task AHalt_IsPublishedAsAFixedCategory_NotItsFreeTextReason()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.EngageKillSwitchAsync("Open orders the engine didn't send: venue-123.", ExecutionService.ReconcilerBy, CancellationToken.None);
        var store = new InMemoryEngineStatusStore();

        await Service(rig, store, new DependencyHealth()).WriteOnceAsync(CancellationToken.None);

        var status = store.Last!;
        Assert.Equal((true, "reconciler"), (status.Halted, status.HaltedBy));
        Assert.Equal(EngineStatusService.HaltCategoryFor("reconciler"), status.HaltCategory);
        Assert.DoesNotContain("venue-123", System.Text.Json.JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.Null(status.Portfolio); // never valued yet: left out rather than guessed
    }

    [Fact]
    public async Task AnUnreadableExchange_IsReportedAsACode_AndKeepsTheLastSyncTime()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(2));
        rig.Exchange.FailReads = true;
        await rig.Execution.ReconcileAsync(CancellationToken.None);

        var last = rig.Execution.LastReconcile!;
        Assert.Equal(("exchange-unreadable", Rig.T0, Rig.T0.AddMinutes(2)), (last.Problem, last.ReconciledAt, last.AttemptedAt));
    }

    [Fact]
    public async Task AFailedDatabaseWrite_MarksTheDatabaseUnreachable_WithoutThrowing()
    {
        var rig = new Rig(eur: 200m);
        var health = new DependencyHealth();
        var failing = new FailingStatusStore();

        Assert.False(await Service(rig, failing, health).WriteOnceAsync(CancellationToken.None));
        Assert.Equal(("unreachable", "HttpRequestException"), (health.Database.State, health.Database.Problem));

        failing.Fail = false;
        Assert.True(await Service(rig, failing, health).WriteOnceAsync(CancellationToken.None));
        Assert.Equal(("ok", Rig.T0), (health.Database.State, health.Database.OkAt));
    }

    [Fact]
    public async Task Healthz_IsDegraded_WhileTheDatabaseIsUnreachable()
    {
        var tools = new ToolRig();
        var health = new DependencyHealth();
        health.DatabaseFailed(Rig.T0, "HttpRequestException");
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, "http://127.0.0.1:0");
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(builder.Services, health);
        Tools.ToolServerRegistration.AddCixToolServer(builder.Services, new Tools.ToolServerOptions { OperatorKey = ToolRig.OperatorKey }, tools.Specs, tools.Journal);
        await using var app = builder.Build();
        Tools.ToolServerRegistration.UseCixToolServer(app);
        await app.StartAsync();
        var server = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(app.Services);
        var address = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();

        using var response = await http.GetAsync(address + Tools.ToolServerRegistration.HealthEndpoint);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode); // still answers: Docker mustn't restart it for this
        Assert.Equal(("degraded", "unreachable"), (doc.RootElement.GetProperty("status").GetString(), doc.RootElement.GetProperty("database").GetString()));
        await app.StopAsync();
    }

    [Fact]
    public void MarketRows_ListEverySymbol_WithMovesSignalsAndReasons_ButNoPrices()
    {
        var at = Rig.T0;
        var ranked = new[]
        {
            new Domain.Scanning.ScanRow("SOL-EUR", 2, 1.2m, 150m, Domain.MarketData.Trend.Down, 0.0123456m, -0.004m, -0.061234m, 1_000_000m, 3.456m, 0.001m, 0.003m, "trend down",
                "The strategy buys only in an uptrend (EMA20 above EMA50); the trend is down."),
            new Domain.Scanning.ScanRow("ETH-EUR", 1, 2.5m, 3_000m, Domain.MarketData.Trend.Up, 0.008m, 0.002m, 0.03m, 5_000_000m, 1.1m, 0.0005m, 0.0028m, null),
        };
        var scan = new Domain.Scanning.ScanResult(at, ranked,
            [new Domain.Scanning.ScanExclusion("BTC-EUR", "costs-exceed-volatility", "detail")],
            [new Domain.Scanning.EntrySignal("ETH-EUR", 1, 3_000m, 0.001m, 3m, 2_900m, 3_200m, 0.1m, "uptrend pullback")],
            [new Domain.Scanning.ExitSignal("BTC-EUR", 0.001m, 60_000m, "stop", "bid at stop")]);

        var rows = EngineStatusService.MarketRows(scan);

        Assert.Equal(new[] { "ETH-EUR", "SOL-EUR", "BTC-EUR" }, rows.Select(r => r.Symbol));
        Assert.Equal("buy", rows[0].Signal);
        Assert.Equal("up", rows[0].Trend);
        Assert.Equal(0.03m, rows[0].Return24h);
        Assert.Equal(-0.0612m, rows[1].Return24h);
        Assert.Equal(0.0123m, rows[1].Volatility);
        Assert.Equal(3.46m, rows[1].VolumeRatio);
        Assert.Equal("trend down", rows[1].Note);
        Assert.Equal("The strategy buys only in an uptrend (EMA20 above EMA50); the trend is down.", rows[1].Detail);
        Assert.Equal("detail", rows[2].Detail); // an excluded symbol's: the filter's figure and threshold
        Assert.Null(rows[1].Signal);
        Assert.Equal("sell (stop)", rows[2].Signal);
        Assert.Equal("costs-exceed-volatility", rows[2].Note);
        Assert.Null(rows[2].Rank);
        var json = System.Text.Json.JsonSerializer.Serialize(rows);
        foreach (var price in new[] { "3000", "2900", "3200", "60000", "150" })
            Assert.DoesNotContain(price, json, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketRows_ShowMovesForExcludedSymbolsToo()
    {
        var scan = new Domain.Scanning.ScanResult(Rig.T0, [],
            [new Domain.Scanning.ScanExclusion("DOGE-EUR", "low-volume", "detail", 0.012345m, -0.05m), new Domain.Scanning.ScanExclusion("ADA-EUR", "stale", "detail")],
            [], []);

        var rows = EngineStatusService.MarketRows(scan);

        Assert.Equal(("ADA-EUR", (decimal?)null, (decimal?)null), (rows[0].Symbol, rows[0].Return1h, rows[0].Return24h));
        Assert.Equal(("DOGE-EUR", (decimal?)0.0123m, (decimal?)-0.05m, "low-volume"), (rows[1].Symbol, rows[1].Return1h, rows[1].Return24h, rows[1].Note));
    }

    [Fact]
    public async Task Status_ReportsManualRequests_WhenTheServiceRuns()
    {
        var rig = new Rig(eur: 200m);
        var manual = new Manual.ManualRequestService(
            new Manual.InMemoryManualRequestStore("ok"), new Manual.InMemoryManualLedgerStore(), rig.Execution, rig.State, Rig.Limits, rig.Journal, new FixedSpecs(null),
            new Manual.ManualRequestOptions(), new TradingContext(TradingMode.Paper), rig.Time, NullLogger<Manual.ManualRequestService>.Instance);
        await manual.PollOnceAsync(CancellationToken.None);
        var store = new InMemoryEngineStatusStore();

        await new EngineStatusService(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), store, new DependencyHealth(), rig.Time,
            NullLogger<EngineStatusService>.Instance, manual: manual).WriteOnceAsync(CancellationToken.None);

        Assert.Equal(new ManualRequestsStatus("ok", 0, Rig.T0), store.Last!.ManualRequests);
        Assert.Null(store.Last.Filters); // no scanner in this rig
    }

    [Fact]
    public async Task BalanceSnapshots_AreJournalledOncePerUtcHour_AndNotAgainAfterARestartInTheSameHour()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 60_010m, "c-1"), postOnly: false, CancellationToken.None,
            context: new OrderContext("place_order", 59_000m, 62_000m));
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        var store = new InMemoryEngineStatusStore();
        EngineStatusService Engine() => new(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), store,
            new DependencyHealth(), rig.Time, NullLogger<EngineStatusService>.Instance, journal: rig.Journal);
        async Task Minute(EngineStatusService engine, TimeSpan later)
        {
            rig.Time.Advance(later);
            rig.Quote(60_000.00m, 60_010.00m);
            await rig.Execution.SampleAsync(CancellationToken.None); // what the safety monitor does every minute
            await engine.WriteOnceAsync(CancellationToken.None);
        }
        IReadOnlyList<Journal.JournalEntry> Snapshots() => rig.Journal.Entries.Where(e => e.Kind == BalanceSnapshots.Kind).ToList();

        var engine = Engine();
        await Minute(engine, TimeSpan.Zero);                // 12:00
        await Minute(engine, TimeSpan.FromMinutes(59));     // 12:59: the same hour
        Assert.Single(Snapshots());
        await Minute(engine, TimeSpan.FromMinutes(2));      // 13:01
        Assert.Equal(2, Snapshots().Count);
        await Minute(Engine(), TimeSpan.FromMinutes(5));    // 13:06, a restarted engine: the journal already has this hour's
        Assert.Equal(2, Snapshots().Count);

        var first = Snapshots().OrderBy(e => e.At).First();
        var portfolio = store.Last!.Portfolio!;
        Assert.Equal(("engine", Rig.T0), (first.Actor, first.At));
        Assert.Equal("simulated", first.Data!["source"]);
        Assert.Equal("EUR", first.Data["currency"]);
        Assert.Equal("1", first.Data["holdings"]);
        Assert.Equal("30.00", first.Data["value:BTC-EUR"]);
        Assert.Equal(portfolio.Equity, decimal.Parse(first.Data["equity"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(portfolio.Equity - 30.00m, decimal.Parse(first.Data["cash"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.StartsWith("Equity ", first.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceSnapshots_SkipAStaleValuation_AndALiveEngineLabelsItsFiguresAsTheExchanges()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.SampleAsync(CancellationToken.None);
        rig.Time.Advance(BalanceSnapshots.MaxAge + TimeSpan.FromMinutes(1)); // no valuation since: prices may be gone
        var snapshots = new BalanceSnapshots(rig.Journal, new TradingContext(TradingMode.Live), rig.Time, NullLogger.Instance);
        var portfolio = new EngineStatusService(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper),
            new InMemoryEngineStatusStore(), new DependencyHealth(), rig.Time, NullLogger<EngineStatusService>.Instance).Build().Portfolio!;

        Assert.False(await snapshots.WriteIfDueAsync(portfolio, null, CancellationToken.None));
        Assert.False(await snapshots.WriteIfDueAsync(null, null, CancellationToken.None));
        Assert.Empty(rig.Journal.Entries.Where(e => e.Kind == BalanceSnapshots.Kind));

        Assert.True(await snapshots.WriteIfDueAsync(portfolio with { At = rig.Time.GetUtcNow() }, "safe@v2", CancellationToken.None));
        var entry = Assert.Single(rig.Journal.Entries, e => e.Kind == BalanceSnapshots.Kind);
        Assert.Equal(("exchange", "0", "safe@v2"), (entry.Data!["source"], entry.Data["holdings"], entry.SpecVersion));
    }

    [Fact]
    public async Task BalanceSnapshots_AreLeftToTheEngineHoldingTheLease()
    {
        var lease = new FixedLease(held: false); // a standby: its copy of the paper account doesn't move
        var rig = new Rig(eur: 200m, lease: lease);
        await rig.Execution.SampleAsync(CancellationToken.None);
        var engine = new EngineStatusService(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper),
            new InMemoryEngineStatusStore(), new DependencyHealth(), rig.Time, NullLogger<EngineStatusService>.Instance, journal: rig.Journal);

        Assert.True(await engine.WriteOnceAsync(CancellationToken.None)); // the status is still written
        Assert.DoesNotContain(rig.Journal.Entries, e => e.Kind == BalanceSnapshots.Kind);

        lease.IsHeld = true;
        await engine.WriteOnceAsync(CancellationToken.None);
        Assert.Single(rig.Journal.Entries, e => e.Kind == BalanceSnapshots.Kind);
    }

    [Fact]
    public async Task AFailingJournal_LeavesTheStatusAndTheDatabaseHealthAlone_AndTheSnapshotFollowsOnceItRecovers()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.SampleAsync(CancellationToken.None);
        var journal = new FlakyJournal(rig.Journal) { FailQueries = true };
        var health = new DependencyHealth();
        var status = new FailingStatusStore { Fail = false };
        var engine = new EngineStatusService(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper),
            status, health, rig.Time, NullLogger<EngineStatusService>.Instance, journal: journal);

        Assert.True(await engine.WriteOnceAsync(CancellationToken.None));
        journal.FailQueries = false;
        journal.FailAppends = true;
        Assert.True(await engine.WriteOnceAsync(CancellationToken.None));
        Assert.Equal("ok", health.Database.State); // only the status write counts against the database
        Assert.DoesNotContain(rig.Journal.Entries, e => e.Kind == BalanceSnapshots.Kind);

        journal.FailAppends = false;
        status.Fail = true; // and a failed status write doesn't hold the snapshot back
        Assert.False(await engine.WriteOnceAsync(CancellationToken.None));
        Assert.Single(rig.Journal.Entries, e => e.Kind == BalanceSnapshots.Kind);
    }

    [Fact]
    public async Task BalanceSnapshots_StartANewDay_EvenWhenYesterdaysLastHourHasOne()
    {
        var time = new MutableTime(new DateTimeOffset(2026, 10, 22, 23, 50, 0, TimeSpan.Zero));
        var rig = new Rig(eur: 200m, time: time);
        await rig.Execution.SampleAsync(CancellationToken.None);
        var portfolio = new EngineStatusService(rig.Execution, rig.KillSwitch, rig.State, Rig.Limits, new TradingContext(TradingMode.Paper),
            new InMemoryEngineStatusStore(), new DependencyHealth(), time, NullLogger<EngineStatusService>.Instance).Build().Portfolio!;
        var snapshots = new BalanceSnapshots(rig.Journal, new TradingContext(TradingMode.Paper), time, NullLogger.Instance);
        Assert.True(await snapshots.WriteIfDueAsync(portfolio, null, CancellationToken.None)); // 23:50

        time.Advance(TimeSpan.FromMinutes(20)); // 00:10 the next day, in a restarted engine
        var restarted = new BalanceSnapshots(rig.Journal, new TradingContext(TradingMode.Paper), time, NullLogger.Instance);
        Assert.True(await restarted.WriteIfDueAsync(portfolio with { At = time.GetUtcNow() }, null, CancellationToken.None));
        Assert.False(await restarted.WriteIfDueAsync(portfolio with { At = time.GetUtcNow() }, null, CancellationToken.None));

        Assert.Equal(
            new[] { new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 23) },
            rig.Journal.Entries.Where(e => e.Kind == BalanceSnapshots.Kind).Select(e => DateOnly.FromDateTime(e.At.UtcDateTime)));
    }

    /// <summary>A journal whose reads or writes can be made to fail, as a database that doesn't answer.</summary>
    private sealed class FlakyJournal(Journal.IJournal inner) : Journal.IJournal
    {
        public bool FailQueries { get; set; }
        public bool FailAppends { get; set; }

        public Task AppendAsync(Journal.JournalEntry entry, CancellationToken cancellationToken) =>
            FailAppends ? throw new IOException("Journal unreachable.") : inner.AppendAsync(entry, cancellationToken);

        public Task<IReadOnlyList<Journal.JournalEntry>> QueryAsync(Journal.JournalQuery query, CancellationToken cancellationToken) =>
            FailQueries ? throw new HttpRequestException("Connection refused") : inner.QueryAsync(query, cancellationToken);
    }

    private sealed class FailingStatusStore : IEngineStatusStore
    {
        public bool Fail { get; set; } = true;

        public bool UsesDatabase => true;

        public Task SaveAsync(EngineStatus status, CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Connection refused") : Task.CompletedTask;
    }
}
