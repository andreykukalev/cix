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
    public async Task Status_ShowsThePortfolioWithPlans_AndTheLastSync()
    {
        var rig = new Rig(eur: 200m);
        await rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 60_010m, "c-1"), postOnly: false, CancellationToken.None,
            context: new OrderContext("place_order", 59_000m, 62_000m));
        await rig.Execution.ReconcileAsync(CancellationToken.None);
        var store = new InMemoryEngineStatusStore();

        Assert.True(await Service(rig, store, new DependencyHealth()).WriteOnceAsync(CancellationToken.None));

        var status = store.Last!;
        Assert.Equal(("paper", false, Rig.T0, Rig.T0), (status.Mode, status.Halted, status.LastReconciledAt, status.OldestQuoteAt));
        var portfolio = status.Portfolio!;
        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(("BTC-EUR", 30.00m, 59_000m, 62_000m, PositionPlanState.Open), (holding.Symbol, holding.ValueQuote, holding.StopLoss, holding.TakeProfit, holding.PlanStatus)); // reconciled: the fill opened the plan
        Assert.Equal("EUR", portfolio.Currency);
        Assert.Equal(portfolio.Equity - 30.00m, portfolio.Cash);
    }

    [Fact]
    public async Task AFailedDatabaseWrite_MarksTheDatabaseUnreachable_WithoutThrowing()
    {
        var rig = new Rig(eur: 200m);
        var health = new DependencyHealth();
        var failing = new FailingStatusStore();

        Assert.False(await Service(rig, failing, health).WriteOnceAsync(CancellationToken.None));
        Assert.Equal(("unreachable", "HttpRequestException"), (health.Database, health.DatabaseProblem));

        failing.Fail = false;
        Assert.True(await Service(rig, failing, health).WriteOnceAsync(CancellationToken.None));
        Assert.Equal("ok", health.Database);
        Assert.Equal(Rig.T0, health.DatabaseOkAt);
    }

    private sealed class FailingStatusStore : IEngineStatusStore
    {
        public bool Fail { get; set; } = true;

        public bool UsesDatabase => true;

        public Task SaveAsync(EngineStatus status, CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Connection refused") : Task.CompletedTask;
    }
}
