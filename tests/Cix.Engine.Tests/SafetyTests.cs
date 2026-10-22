using Cix.Domain;
using Cix.Domain.Risk;
using Cix.Engine.Execution;

namespace Cix.Engine.Tests;

public sealed class ReconciliationTests
{
    private const string Btc = Rig.Btc;

    [Fact]
    public async Task OrderTheEngineDidNotSend_EngagesTheKillSwitch_AndIsCancelled()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Buy(0.0005m, 59_950m, "ours");
        await rig.Paper.PlaceLimitOrderAsync(new OrderIntent(Btc, OrderSide.Buy, 0.0005m, 59_900m, "someone-else").Approved(), false, CancellationToken.None);

        var report = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Single(report.UnknownOrders);
        Assert.True(rig.KillSwitch.IsEngaged);
        Assert.Equal(ExecutionService.ReconcilerBy, rig.KillSwitch.State.By);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task OwnOrders_AreRecognised_AndForgottenOnceFinished()
    {
        var rig = new Rig(eur: 1_000m);
        var placed = await rig.Buy(0.0005m, 59_950m, "a");
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        Assert.Equal(new[] { "a" }, rig.Store.Current.Known);

        await rig.Paper.CancelOrderAsync(placed.Placed!.VenueOrderId, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(3)); // past the pending window
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // missing once: kept
        var keptAfterOne = rig.Store.Current.Known.ToList();
        await rig.Execution.ReconcileAsync(CancellationToken.None); // missing twice: forgotten

        Assert.False(rig.KillSwitch.IsEngaged);
        Assert.Equal(new[] { "a" }, keptAfterOne);
        Assert.Empty(rig.Store.Current.Known);
    }

    [Fact]
    public async Task Deposit_EngagesTheKillSwitch_ButAFillDoesNot()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // baseline

        var fill = await rig.Buy(0.0005m, 60_010m, "taker"); // crosses the ask: fills at once
        var afterFill = await rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Exchange.ExtraBalances["EUR"] = 50m;
        var afterDeposit = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Placed, fill.Status);
        Assert.Null(afterFill.KillSwitchReason);
        Assert.InRange(afterFill.UnexplainedChangeQuote!.Value, -0.1m, 0m); // the taker fee and half the spread
        Assert.Equal(50m, afterDeposit.UnexplainedChangeQuote);
        Assert.True(rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task UncertainPlacement_ListedLater_IsResolved()
    {
        var rig = new Rig();
        rig.Exchange.HideOpenOrders = true;
        rig.Exchange.UncertainAfterSending = true; // it arrived, but the lookups can't see it yet
        var result = await rig.Buy(0.0005m, 59_950m, "late");
        rig.Exchange.HideOpenOrders = false;
        rig.Exchange.UncertainAfterSending = null;

        var report = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Uncertain, result.Status);
        Assert.Equal(1, report.ResolvedUncertain);
        Assert.Empty(rig.Execution.Unresolved);
        Assert.False(rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task UncertainPlacement_NeverListed_IsDroppedAfterTheTimeout()
    {
        var rig = new Rig();
        rig.Exchange.UncertainAfterSending = false; // never arrived
        await rig.Buy(0.0005m, 59_950m, "lost");
        rig.Exchange.UncertainAfterSending = null;

        var early = await rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Time.Advance(rig.Safety.UncertainTimeout);
        rig.Quote(60_000.00m, 60_010.00m);
        var late = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal((0, 0), (early.ResolvedUncertain, early.DroppedUncertain));
        Assert.Equal(1, late.DroppedUncertain);
        Assert.Empty(rig.Execution.Unresolved);
        Assert.Equal("duplicate-client-order-id", (await rig.Buy(0.0005m, 59_950m, "lost")).Rule); // still never reused
    }
}

public sealed class RestartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cix-restart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private FileEngineStateStore Store(MutableTime time) => new(Path.Combine(_dir, "state.json"), time);

    private async Task<Rig> FirstRunAsync()
    {
        var time = new MutableTime(Rig.T0);
        var store = Store(time);
        var rig = new Rig(eur: 1_000m, store: store, time: time);
        rig.Execution.ReleaseKillSwitch("owner"); // a new state file starts engaged
        return await Task.FromResult(rig);
    }

    [Fact]
    public async Task RestartWithAnOpenOrder_RecoversItAsOurs()
    {
        var first = await FirstRunAsync();
        await first.Buy(0.0006m, 59_950m, "before-restart");

        var second = first.Restart(() => Store(first.Time));
        second.Quote(60_000.00m, 60_010.00m);
        var report = await second.Execution.ReconcileAsync(CancellationToken.None);
        var snapshot = await second.Snapshot();

        Assert.Empty(report.UnknownOrders);
        Assert.False(second.KillSwitch.IsEngaged);
        Assert.Equal(35.97m, snapshot.TotalExposureQuote); // read back from the exchange
        Assert.Equal("duplicate-client-order-id", (await second.Buy(0.0006m, 59_950m, "before-restart")).Rule);
    }

    [Fact]
    public async Task CrashMidPlacement_TheOrderIsStillOursAfterRestart()
    {
        var first = await FirstRunAsync();
        first.Exchange.CrashAfterSending = true;

        await Assert.ThrowsAsync<SimulatedCrashException>(() => first.Buy(0.0006m, 59_950m, "mid-flight"));

        var second = first.Restart(() => Store(first.Time));
        second.Quote(60_000.00m, 60_010.00m);
        var report = await second.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Empty(report.UnknownOrders);
        Assert.False(second.KillSwitch.IsEngaged);
        Assert.Single(await second.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task EngagedKillSwitch_StaysEngagedAfterRestart()
    {
        var first = await FirstRunAsync();
        await first.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);

        var second = first.Restart(() => Store(first.Time));

        Assert.True(second.KillSwitch.IsEngaged);
        Assert.Equal("kill-switch", (await second.Buy(0.0006m, 59_950m, "after")).Rule);
    }
}

public sealed class SafetyMonitorTests
{
    [Fact]
    public async Task Drawdown_AtTheLimit_EngagesTheKillSwitch()
    {
        var rig = new Rig(eur: 100m, btc: 0.002m);
        await rig.Monitor.CheckOnceAsync(CancellationToken.None); // peak 220.01

        rig.Quote(40_000.00m, 40_010.00m); // equity 180.01: 18.2% down
        var reason = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        Assert.StartsWith("Drawdown", reason, StringComparison.Ordinal);
        Assert.True(rig.KillSwitch.IsEngaged);
        Assert.Equal(SafetyMonitor.By, rig.KillSwitch.State.By);
    }

    [Fact]
    public async Task StaleMarketData_EngagesTheKillSwitch_OnlyAfterTheGracePeriod()
    {
        var rig = new Rig();
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        var early = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromMinutes(2)); // no quote for 16 minutes
        var late = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Null(early);
        Assert.StartsWith("No fresh market data", late, StringComparison.Ordinal);
        Assert.True(rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task HealthyEngine_StaysRunning_AndReconcilesOnSchedule()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Buy(0.0005m, 59_950m, "a");

        for (var minute = 0; minute < 20; minute++)
        {
            Assert.Null(await rig.Monitor.CheckOnceAsync(CancellationToken.None));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            rig.Quote(60_000.00m, 60_010.00m);
        }

        Assert.False(rig.KillSwitch.IsEngaged);
        Assert.Equal(new[] { "a" }, rig.Store.Current.Known);
    }

    [Fact]
    public async Task ConsecutiveGateRejections_EngageTheKillSwitch()
    {
        var rig = new Rig(safety: new SafetyOptions { MaxConsecutiveRejects = 3 });

        for (var i = 0; i < 3; i++)
            Assert.Equal("price-band", (await rig.Buy(0.0005m, 70_000m, $"far-{i}")).Rule);

        Assert.True(rig.KillSwitch.IsEngaged);
        Assert.Equal(ExecutionService.RejectStreakBy, rig.KillSwitch.State.By);
    }

    [Fact]
    public async Task APlacedOrder_ResetsTheRejectStreak()
    {
        var rig = new Rig(safety: new SafetyOptions { MaxConsecutiveRejects = 3 });

        await rig.Buy(0.0005m, 70_000m, "far-1");
        await rig.Buy(0.0005m, 70_000m, "far-2");
        await rig.Buy(0.0005m, 59_950m, "ok");
        await rig.Buy(0.0005m, 70_000m, "far-3");

        Assert.False(rig.KillSwitch.IsEngaged);
    }
}
