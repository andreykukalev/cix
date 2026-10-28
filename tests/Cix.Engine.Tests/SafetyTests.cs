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
        await rig.Execution.ReconcileAsync(CancellationToken.None); // first seen missing: kept
        await rig.Execution.ReconcileAsync(CancellationToken.None); // an extra call at once doesn't shorten the wait
        var keptAfterOne = rig.Store.Current.Known.ToList();
        rig.Time.Advance(rig.Safety.ReconcileInterval);
        rig.Quote(60_000.00m, 60_010.00m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // missing for a full interval: forgotten

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
        var firstMiss = await rig.Execution.ReconcileAsync(CancellationToken.None);
        var secondMiss = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal((0, 0), (early.ResolvedUncertain, early.DroppedUncertain));
        Assert.Equal(0, firstMiss.DroppedUncertain); // one listing alone could be lagging
        Assert.Equal(1, secondMiss.DroppedUncertain);
        Assert.Empty(rig.Execution.Unresolved);
        Assert.Equal("duplicate-client-order-id", (await rig.Buy(0.0005m, 59_950m, "lost")).Rule); // still never reused
    }
}

public sealed class FailureTests
{
    private sealed class FailingStore : IEngineStateStore
    {
        private readonly InMemoryEngineStateStore _inner = new();

        public bool FailWrites { get; set; }

        public EngineState Current => _inner.Current;

        /// <summary>Like the file store, only a real change writes, so only a real change can fail.</summary>
        public EngineState Update(Func<EngineState, EngineState> change)
        {
            var next = change(Current);
            if (next == Current)
                return Current;
            return FailWrites ? throw new IOException("Disk full.") : _inner.Update(_ => next);
        }
    }

    [Fact]
    public async Task OrderThatCannotBeRecorded_IsNotSent()
    {
        var store = new FailingStore();
        var rig = new Rig(store: store);
        await rig.Snapshot(); // baselines written while the disk works
        store.FailWrites = true;

        var result = await rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 59_950m, "x"), false, CancellationToken.None);

        Assert.Equal("state-unavailable", result.Rule);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeUnavailable_SkipsReconciliation_WithoutTriggering()
    {
        var rig = new Rig();
        rig.Exchange.FailReads = true;

        var report = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Same(ReconcileReport.Skipped, report);
        Assert.False(rig.KillSwitch.IsEngaged);
    }
}

public sealed class HoldingsCheckTests
{
    [Fact]
    public async Task ChangeDuringAStalePriceGap_IsComparedLater_NotAbsorbed()
    {
        var rig = new Rig(btc: 0.001m);
        await rig.Execution.ReconcileAsync(CancellationToken.None); // baseline

        rig.Time.Advance(TimeSpan.FromMinutes(6)); // BTC price now stale
        rig.Exchange.ExtraBalances["BTC"] = 0.001m; // BTC deposited from outside
        var duringGap = await rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Quote(60_000.00m, 60_010.00m);
        var afterGap = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Null(duringGap.UnexplainedChangeQuote);
        Assert.Null(duringGap.KillSwitchReason);
        Assert.Equal(60.005m, afterGap.UnexplainedChangeQuote);
        Assert.True(rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task LargerFills_GetAProportionalAllowance()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        await rig.Buy(0.0006m, 60_010m, "taker"); // 36.01 EUR, fills at once
        rig.Quote(60_600.00m, 60_610.00m); // the price then moves 1% before the next reconciliation

        var report = await rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.InRange(report.UnexplainedChangeQuote!.Value, 0.3m, 0.4m); // 0.0006 BTC up 595 per BTC since the fill, minus the 0.03 fee
        Assert.Null(report.KillSwitchReason);
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
        var rig = new Rig(eur: 1_000m, store: Store(time), time: time);
        await rig.Execution.ReleaseKillSwitchAsync("owner"); // a new state file starts engaged
        return rig;
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
    public async Task DepositWhileTheEngineWasDown_IsCaughtAfterRestart()
    {
        var first = await FirstRunAsync();
        await first.Execution.ReconcileAsync(CancellationToken.None); // baseline persisted

        var second = first.Restart(() => Store(first.Time));
        second.Exchange.ExtraBalances["EUR"] = 100m;
        var report = await second.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal(100m, report.UnexplainedChangeQuote);
        Assert.True(second.KillSwitch.IsEngaged);
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
    public async Task DrawdownAtTheLimit_CancelsBuys_ButLeavesSells_AndTheHardCeilingHalts()
    {
        var rig = new Rig(eur: 200m, btc: 0.001m); // equity 260.005
        await rig.Buy(0.0003m, 59_950m, "buy");
        var sell = await rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Sell, 0.0005m, 60_010m, "sell"), false, CancellationToken.None);

        rig.Store.Update(s => s with { PeakEquity = 320m }); // 18.75% down: over the 15% limit
        var atLimit = await rig.Monitor.CheckOnceAsync(CancellationToken.None);
        var stillOpen = await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None);

        rig.Store.Update(s => s with { PeakEquity = 400m }); // 35% down: over the 30% hard ceiling
        var atCeiling = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Placed, sell.Status);
        Assert.Null(atLimit.KillSwitchReason);
        Assert.Equal(1, atLimit.BuysCancelled);
        Assert.Equal(OrderSide.Sell, Assert.Single(stillOpen).Side);
        Assert.StartsWith("Critical: drawdown", atCeiling.KillSwitchReason, StringComparison.Ordinal);
        Assert.True(rig.KillSwitch.IsEngaged);
        Assert.Equal(SafetyMonitor.CriticalBy, rig.KillSwitch.State.By);
    }

    [Fact]
    public async Task HardCeilingOverAnotherHalt_IsRecordedBesideIt_OnceNotEveryMinute()
    {
        var rig = new Rig(eur: 200m, btc: 0.001m);
        await rig.Execution.EngageKillSwitchAsync("Paused by the owner from the dashboard (request p-1).", "owner", CancellationToken.None);

        rig.Store.Update(s => s with { PeakEquity = 400m }); // 35% down while paused
        await rig.Monitor.CheckOnceAsync(CancellationToken.None);
        var also = rig.KillSwitch.State.AlsoAt;
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(("owner", SafetyMonitor.CriticalBy), (rig.KillSwitch.State.By, rig.KillSwitch.State.AlsoBy));
        Assert.Equal(also, rig.KillSwitch.State.AlsoAt); // the same finding a minute later changes nothing
        Assert.Single(rig.Journal.Entries, e => e.Kind == "kill-switch" && e.Data!["action"] == "also-triggered");
    }

    [Fact]
    public async Task StaleMarketData_EngagesTheKillSwitch_OnlyAfterTheGracePeriod()
    {
        var rig = new Rig();
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        var early = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromMinutes(2)); // no quote for 16 minutes
        var late = await rig.Monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Null(early.KillSwitchReason);
        Assert.StartsWith("No fresh market data", late.KillSwitchReason, StringComparison.Ordinal);
        Assert.True(rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task HealthyEngine_StaysRunning_AndReconcilesOnSchedule()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Buy(0.0005m, 59_950m, "a");

        for (var minute = 0; minute < 20; minute++)
        {
            Assert.Equal(new SafetyCheckResult(null, 0), await rig.Monitor.CheckOnceAsync(CancellationToken.None));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            rig.Quote(60_000.00m, 60_010.00m);
        }

        Assert.False(rig.KillSwitch.IsEngaged);
        Assert.Equal(new[] { "a" }, rig.Store.Current.Known);
    }

    [Fact]
    public async Task Reconciliation_RunsOnSchedule_CatchingADepositAtTheNextRun()
    {
        var rig = new Rig();
        var results = new List<SafetyCheckResult>();
        for (var minute = 0; minute <= 4; minute++)
        {
            results.Add(await rig.Monitor.CheckOnceAsync(CancellationToken.None)); // reconciles at minutes 0, 2 and 4
            if (minute == 2)
                rig.Exchange.ExtraBalances["EUR"] = 50m; // deposited after the minute-2 run
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            rig.Quote(60_000.00m, 60_010.00m);
        }

        Assert.All(results.Take(4), r => Assert.Null(r.KillSwitchReason)); // minute 3 doesn't reconcile
        Assert.StartsWith("Holdings changed", results[4].KillSwitchReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineSideRejects_DoNotCountTowardTheStreak()
    {
        var rig = new Rig(eur: 1_000m, safety: new SafetyOptions { MaxConsecutiveRejects = 3 });
        await rig.Buy(0.0005m, 59_950m, "ok");
        await rig.Buy(0.0005m, 70_000m, "far-1");
        await rig.Buy(0.0005m, 70_000m, "far-2"); // gate streak: 2

        for (var i = 0; i < 3; i++)
            Assert.Equal("duplicate-client-order-id", (await rig.Buy(0.0005m, 59_950m, "ok")).Rule);
        var engagedByDuplicates = rig.KillSwitch.IsEngaged;
        await rig.Buy(0.0005m, 70_000m, "far-3"); // the third gate rejection

        Assert.False(engagedByDuplicates);
        Assert.True(rig.KillSwitch.IsEngaged);
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

    [Fact]
    public async Task Release_ResetsTheRejectStreak()
    {
        var rig = new Rig(safety: new SafetyOptions { MaxConsecutiveRejects = 3 });
        for (var i = 0; i < 3; i++)
            await rig.Buy(0.0005m, 70_000m, $"far-{i}");

        await rig.Execution.ReleaseKillSwitchAsync("owner");
        await rig.Buy(0.0005m, 70_000m, "far-after");

        Assert.False(rig.KillSwitch.IsEngaged);
    }

    [Theory]
    [InlineData(20, 2, 10)]  // tolerance above 5% of a 200 allocation
    [InlineData(2, 1, 10)]   // uncertain timeout shorter than the 2-minute reconcile interval
    [InlineData(2, 10, 0)]   // reject streak trigger switched off
    public void OptionsThatWouldDisableATrigger_AreRefused(int tolerance, int uncertainMinutes, int maxRejects)
    {
        var options = new SafetyOptions
        {
            BalanceToleranceQuote = tolerance,
            UncertainTimeout = TimeSpan.FromMinutes(uncertainMinutes),
            MaxConsecutiveRejects = maxRejects,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(Rig.Limits));
    }
}
