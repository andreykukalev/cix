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
            new Domain.Scanning.ScanRow("SOL-EUR", 2, 1.2m, 150m, Domain.MarketData.Trend.Down, 0.0123456m, -0.004m, -0.061234m, 1_000_000m, 3.456m, 0.001m, 0.003m, "trend down"),
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
        Assert.Null(rows[1].Signal);
        Assert.Equal("sell (stop)", rows[2].Signal);
        Assert.Equal("costs-exceed-volatility", rows[2].Note);
        Assert.Null(rows[2].Rank);
        var json = System.Text.Json.JsonSerializer.Serialize(rows);
        foreach (var price in new[] { "3000", "2900", "3200", "60000", "150" })
            Assert.DoesNotContain(price, json, StringComparison.Ordinal);
    }

    private sealed class FailingStatusStore : IEngineStatusStore
    {
        public bool Fail { get; set; } = true;

        public bool UsesDatabase => true;

        public Task SaveAsync(EngineStatus status, CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Connection refused") : Task.CompletedTask;
    }
}
