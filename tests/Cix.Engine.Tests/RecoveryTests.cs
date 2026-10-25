using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Microsoft.Azure.Cosmos;
using Cix.Engine.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Cix.Engine.Tests;

/// <summary>
/// Restarts at any point keep the engine's orders, plans and journal consistent: nothing is placed twice, no fill is
/// journaled twice or lost, and every position keeps its stop and target (docs/persistence.md).
/// </summary>
public sealed class RecoveryTests
{
    private static readonly OrderContext Plan = new("place_order", 59_000m, 62_000m, "run-1", "good@v1#abc123def456");

    private static Task<ExecutionResult> BuyWithPlan(Rig rig, decimal price, string id = "c-1") =>
        rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, price, id), postOnly: false, CancellationToken.None, context: Plan);

    [Fact]
    public async Task ThePlanIsSavedBeforeTheBuyIsSent_SoAScanRightAfterTheFillSeesItsStop()
    {
        var rig = new Rig(eur: 200m);

        var result = await BuyWithPlan(rig, 60_010m); // crosses the ask: fills at once, before any reconciliation journals it
        var plans = await PositionPlans.LoadAsync(rig.Execution, rig.Journal, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Placed, result.Status);
        Assert.DoesNotContain(rig.Journal.Entries, e => e.Kind == "fill"); // the journal hasn't caught up yet
        var plan = Assert.Single(plans!);
        Assert.Equal((59_000m, 62_000m), (plan.StopLoss, plan.TakeProfit)); // the XRP case: no stop meant a trend exit
        var state = rig.Store.Current.Plans[Rig.Btc];
        Assert.Equal((PositionPlanState.EntryPending, "c-1"), (state.Status, state.EntryClientOrderId));
    }

    [Fact]
    public async Task SentOrders_AreReadable_AndCarryTheirExchangeId()
    {
        var rig = new Rig(eur: 200m);

        var result = await BuyWithPlan(rig, 59_950m); // rests below the ask

        var sent = Assert.Single(rig.Store.Current.SentOrders);
        Assert.Equal(
            ("c-1", Rig.Btc, "buy", 0.0005m, 59_950m, "place_order", result.Placed!.VenueOrderId, 59_000m, "run-1"),
            (sent.ClientOrderId, sent.Symbol, sent.Side, sent.Size, sent.LimitPrice, sent.Source, sent.VenueOrderId, sent.StopLoss, sent.RunId));
    }

    [Fact]
    public async Task AFillBeforeARestart_IsJournaledOnceAfterIt_AndOpensThePlan()
    {
        var first = new Rig(eur: 200m);
        await BuyWithPlan(first, 60_010m); // filled; the engine "crashes" before reconciling

        var second = first.Restart(() => first.Store);
        second.Quote(60_000.00m, 60_010.00m);
        await second.Execution.ReconcileAsync(CancellationToken.None);
        var third = second.Restart(() => second.Store); // and once more, before the order is forgotten
        third.Quote(60_000.00m, 60_010.00m);
        await third.Execution.ReconcileAsync(CancellationToken.None);

        var fill = Assert.Single(third.Journal.Entries, e => e.Kind == "fill");
        Assert.Equal(("c-1", "place_order", "59000", "run-1", "good@v1#abc123def456"),
            (fill.Data!["clientOrderId"], fill.Data["source"], fill.Data["stopLoss"], fill.RunId, fill.SpecVersion));
        var plan = third.Store.Current.Plans[Rig.Btc];
        Assert.Equal((PositionPlanState.Open, 60_010m, 59_000m), (plan.Status, plan.EntryPrice, plan.StopLoss));
    }

    [Fact]
    public async Task ABuyThatEndsUnfilled_TakesItsPlanWithIt()
    {
        var rig = new Rig(eur: 200m);
        var placed = await BuyWithPlan(rig, 59_950m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // working: the plan waits for it
        Assert.True(rig.Store.Current.Plans.ContainsKey(Rig.Btc));

        await rig.Paper.CancelOrderAsync(placed.Placed!.VenueOrderId, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(3)); // past the pending window
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Empty(rig.Store.Current.Plans);
        Assert.Contains(rig.Journal.Entries, e => e.Kind == "order-closed" && e.Data!["clientOrderId"] == "c-1");
    }

    [Fact]
    public async Task ASoldPosition_LosesItsPlan_ButAHeldOneKeepsItAcrossRestarts()
    {
        var rig = new Rig(eur: 200m);
        await BuyWithPlan(rig, 60_010m);
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        var restarted = rig.Restart(() => rig.Store);
        restarted.Quote(60_000.00m, 60_010.00m);
        await restarted.Execution.ReconcileAsync(CancellationToken.None);
        Assert.Equal(59_000m, restarted.Store.Current.Plans[Rig.Btc].StopLoss); // still held: still planned

        var sold = await restarted.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Sell, 0.0005m, 60_000m, "s-1"), postOnly: false, CancellationToken.None);
        restarted.Time.Advance(TimeSpan.FromMinutes(3));
        restarted.Quote(60_000.00m, 60_010.00m);
        await restarted.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Placed, sold.Status);
        Assert.Empty(restarted.Store.Current.Plans);
    }

    [Fact]
    public async Task AFurtherBuy_ChangesAnOpenPlanOnlyIfItFills()
    {
        var rig = new Rig(eur: 200m);
        await BuyWithPlan(rig, 60_010m);
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // open, stop 59,000

        // A post-only buy that would cross is rejected: its stop (just under the market) must not replace the open one.
        var refused = await rig.Execution.PlaceAsync(
            new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 60_010m, "c-2"), postOnly: true, CancellationToken.None,
            context: Plan with { StopLoss = 59_990m });
        var afterRefusal = rig.Store.Current.Plans[Rig.Btc];

        var filled = await rig.Execution.PlaceAsync(
            new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 60_010m, "c-3"), postOnly: false, CancellationToken.None,
            context: Plan with { StopLoss = 59_500m });
        Assert.Equal(59_000m, rig.Store.Current.Plans[Rig.Btc].StopLoss); // still the old stop until the fill is confirmed
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal((ExecutionStatus.Failed, ExecutionStatus.Placed), (refused.Status, filled.Status));
        Assert.Equal((59_000m, "c-1", (string?)null), (afterRefusal.StopLoss, afterRefusal.EntryClientOrderId, afterRefusal.PendingClientOrderId));
        var plan = rig.Store.Current.Plans[Rig.Btc];
        Assert.Equal((59_500m, "c-3", (string?)null), (plan.StopLoss, plan.EntryClientOrderId, plan.PendingClientOrderId));
    }

    [Fact]
    public async Task ARefusedBuy_LeavesNoOrderOrPlanBehind()
    {
        var rig = new Rig(eur: 200m);
        rig.Exchange.RefuseNext = true;

        var result = await BuyWithPlan(rig, 59_950m);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(rig.Store.Current.SentOrders);
        Assert.Empty(rig.Store.Current.Plans);
    }

    [Fact]
    public async Task RestoringThePaperAccount_CanBeRetried_AndHappensOnce()
    {
        var time = new MutableTime(Rig.T0);
        var store = new Persistence.InMemoryPaperAccountStore();
        var options = new Paper.PaperOptions();
        options.InitialBalances["EUR"] = 123m;
        var before = new Paper.PaperExchange(new FakeMarketData(), options, time);
        await new Persistence.PaperAccountSaver(before, store, time, NullLogger<Persistence.PaperAccountSaver>.Instance).SaveIfChangedAsync(CancellationToken.None);

        var after = new Paper.PaperExchange(new FakeMarketData(), new Paper.PaperOptions(), time);
        var saver = new Persistence.PaperAccountSaver(after, store, time, NullLogger<Persistence.PaperAccountSaver>.Instance);

        Assert.True(await saver.RestoreAsync(CancellationToken.None));
        Assert.True(await saver.RestoreAsync(CancellationToken.None)); // a retried startup doesn't restore twice (that would throw)
        Assert.Equal(123m, (await after.GetBalancesAsync(CancellationToken.None)).Single(b => b.Currency == "EUR").Available);
    }

    [Fact]
    public void OlderStateWithBareIds_IsStillUnderstood()
    {
        var legacy = EngineState.Initial with { KnownClientOrderIds = ["old-1"] };
        var sent = new SentOrder("new-1", Rig.Btc, "sell", 0.001m, 61_000m, Rig.T0, "protective-exit");

        var next = legacy.WithSent(sent);

        Assert.Equal(new[] { "new-1", "old-1" }, next.Known);
        Assert.Equal(new[] { "new-1" }, next.WithoutOrders(["old-1"]).Known);
        Assert.Null(next.WithoutOrders(["old-1"]).KnownClientOrderIds);
        Assert.Empty(next.Plans); // a sell carries no plan
    }

    [Fact]
    public void JournalIds_ReadInOrder_AndSayWhatTheyAre()
    {
        var id = JournalIds.New(new DateTimeOffset(2026, 10, 25, 12, 46, 50, TimeSpan.Zero), "order");

        Assert.Matches(new Regex("^20261025-124650-order-[0-9a-f]{12}$"), id);
        Assert.NotEqual(id, JournalIds.New(new DateTimeOffset(2026, 10, 25, 12, 46, 50, TimeSpan.Zero), "order"));
        Assert.StartsWith("20261025-124650-scan-signal-", JournalIds.New(new DateTimeOffset(2026, 10, 25, 12, 46, 50, TimeSpan.Zero), "scan-signal"), StringComparison.Ordinal);
    }
}

public sealed class StartupRetryTests
{
    [Fact]
    public async Task WaitsForTheDatabase_WithGrowingPauses_ThenCarriesOn()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();

        await StartupRetry.RunAsync(
            _ => ++attempts < 3 ? throw new HttpRequestException("connection refused") : Task.CompletedTask,
            "the database", NullLogger.Instance, TimeProvider.System, CancellationToken.None,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, waits);
        Assert.Equal(TimeSpan.FromSeconds(30), StartupRetry.Backoff(9));
    }

    [Fact]
    public async Task AFailureTimeCantFix_StopsStartupAtOnce()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupRetry.RunAsync(
            _ => { attempts++; throw new InvalidOperationException("The engine state document has no kill switch state."); },
            "the database", NullLogger.Instance, TimeProvider.System, CancellationToken.None, (_, _) => Task.CompletedTask));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TransientFailures_AreTheOnesWaitingCanFix()
    {
        var none = CancellationToken.None;
        Assert.True(StartupRetry.IsTransient(new CosmosException("starting", HttpStatusCode.ServiceUnavailable, 0, "a", 0), none));
        Assert.False(StartupRetry.IsTransient(new CosmosException("bad key", HttpStatusCode.Unauthorized, 0, "a", 0), none));
        Assert.True(StartupRetry.IsTransient(new IOException("Engine state could not be saved.", new HttpRequestException("refused")), none));
        Assert.True(StartupRetry.IsTransient(new SocketException(), none));
        Assert.False(StartupRetry.IsTransient(new FileNotFoundException("risk-limits.json"), none));
        Assert.False(StartupRetry.IsTransient(new IOException("changed elsewhere", new InvalidOperationException()), none));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(StartupRetry.IsTransient(new OperationCanceledException(), cancelled.Token));
    }
}

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Healthz_AnswersOnceListening_WithModeAndHaltedState()
    {
        var tools = new ToolRig();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new TradingContext(TradingMode.Paper));
        builder.Services.AddSingleton(tools.Rig.KillSwitch);
        builder.Services.AddCixToolServer(new ToolServerOptions { OperatorKey = ToolRig.OperatorKey }, tools.Specs, tools.Journal);
        await using var app = builder.Build();
        app.UseCixToolServer();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();

        using var doc = JsonDocument.Parse(await http.GetStringAsync(address + ToolServerRegistration.HealthEndpoint));

        var root = doc.RootElement;
        Assert.Equal(("ready", "paper", false), (root.GetProperty("status").GetString(), root.GetProperty("mode").GetString(), root.GetProperty("halted").GetBoolean()));
        await app.StopAsync();
    }
}
