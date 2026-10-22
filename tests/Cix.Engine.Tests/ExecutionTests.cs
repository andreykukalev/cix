using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>Wraps an exchange so tests can make placement uncertain or hide open orders, like a slow or flaky venue.</summary>
internal sealed class FlakyExchange(IExchangeClient inner) : IExchangeClient
{
    /// <summary>Throw an uncertain-outcome error; <c>true</c> = after the order really reached the exchange.</summary>
    public bool? UncertainAfterSending { get; set; }

    public bool HideOpenOrders { get; set; }

    public int OpenOrderLookups { get; private set; }

    public Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken) => inner.GetBalancesAsync(cancellationToken);

    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) => inner.GetPairsAsync(cancellationToken);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
        inner.GetCandlesAsync(symbol, intervalMinutes, since, cancellationToken);

    public async Task<PlacedOrder> PlaceLimitOrderAsync(ApprovedOrder order, bool postOnly, CancellationToken cancellationToken)
    {
        if (UncertainAfterSending is null)
            return await inner.PlaceLimitOrderAsync(order, postOnly, cancellationToken);
        if (UncertainAfterSending == true)
            await inner.PlaceLimitOrderAsync(order, postOnly, cancellationToken);
        throw new OrderPlacementUncertainException(order.ClientOrderId);
    }

    public Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken) => inner.GetOrderAsync(venueOrderId, cancellationToken);

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken)
    {
        OpenOrderLookups++;
        return HideOpenOrders ? [] : await inner.GetOpenOrdersAsync(symbol, cancellationToken);
    }

    public Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken) => inner.CancelOrderAsync(venueOrderId, cancellationToken);
}

public sealed class ExecutionTests
{
    private const string Btc = "BTC-EUR";
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(decimal eur = 200m, decimal btc = 0m, IEngineStateStore? store = null, IMarketDataSource? pairSource = null)
        {
            var options = new PaperOptions();
            options.InitialBalances["EUR"] = eur;
            options.InitialBalances["BTC"] = btc;
            Paper = new PaperExchange(new FakeMarketData(), options, Time);
            Exchange = new FlakyExchange(Paper);
            State = new MarketState(Time);
            Store = store ?? new InMemoryEngineStateStore();
            KillSwitch = new KillSwitch(Store, Time);
            var pairs = new PairCatalog(pairSource ?? new FakeMarketData(), Time);
            Portfolio = new PortfolioService(Exchange, pairs, State, KillSwitch, Store, Limits, Time);
            Execution = new ExecutionService(
                Exchange, new RiskGate(Limits, Time), Portfolio, State, pairs, KillSwitch, Time,
                NullLogger<ExecutionService>.Instance, (_, _) => Task.CompletedTask);
            Quote(60_000.00m, 60_010.00m);
        }

        public static RiskLimits Limits { get; } = new(200m, 40m, 100m, 15m, 0.15m, 4, 0.01m, new HashSet<string> { Btc });

        public MutableTime Time { get; } = new(T0);
        public MarketState State { get; }
        public PaperExchange Paper { get; }
        public FlakyExchange Exchange { get; }
        public IEngineStateStore Store { get; }
        public KillSwitch KillSwitch { get; }
        public PortfolioService Portfolio { get; }
        public ExecutionService Execution { get; }

        /// <summary>A fresh quote at the current time, to both the engine's market state and the paper exchange.</summary>
        public void Quote(decimal bid, decimal ask)
        {
            var quote = new MarketQuote(Btc, bid, 0.01m, ask, 0.01m, Time.GetUtcNow());
            State.Update(quote);
            Paper.OnQuote(quote);
        }

        public Task<ExecutionResult> Buy(decimal size, decimal price, string id = "c-1") =>
            Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Buy, size, price, id), postOnly: false, CancellationToken.None);

        public Task<PortfolioSnapshot> Snapshot() => Portfolio.GetSnapshotAsync(0, [], CancellationToken.None);
    }

    [Fact]
    public async Task OrderWithinLimits_IsRoundedApprovedAndPlaced()
    {
        var rig = new Rig();

        var result = await rig.Buy(0.0005m, 59_950.004m);

        Assert.Equal(ExecutionStatus.Placed, result.Status);
        Assert.Equal(59_950.00m, result.Order!.LimitPrice); // buys round down to the 0.01 step
        var open = Assert.Single(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal(result.Placed!.VenueOrderId, open.VenueOrderId);
    }

    [Fact]
    public async Task OversizedOrder_IsResizedByTheGate_BeforeItIsSent()
    {
        var rig = new Rig();

        var result = await rig.Buy(0.002m, 59_950m); // 119.90 EUR; max order 40

        Assert.Equal(ExecutionStatus.Placed, result.Status);
        Assert.Equal("max-order", result.Rule);
        Assert.Equal(0.00066722m, result.Order!.BaseSize);
        Assert.Equal(0.00066722m, Assert.Single(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).Quantity);
    }

    [Fact]
    public async Task RejectedOrder_IsNeverSent()
    {
        var rig = new Rig();

        var result = await rig.Buy(0.0005m, 62_000m); // 3.3% above the market; band is 1%

        Assert.Equal(ExecutionStatus.Rejected, result.Status);
        Assert.Equal("price-band", result.Rule);
        Assert.Null(result.Order);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task OpenBuys_CountTowardThePositionCap()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Buy(0.0006m, 59_950m, "a"); // 35.97 EUR each, resting
        await rig.Buy(0.0006m, 59_950m, "b");

        var result = await rig.Buy(0.0006m, 59_950m, "c"); // 71.94 working, 28.06 left of the 100 cap

        Assert.Equal("position-cap", result.Rule);
        Assert.Equal(0.00046805m, result.Order!.BaseSize);
    }

    [Fact]
    public async Task OrderRate_IsCountedByTheEngine()
    {
        var rig = new Rig(eur: 1_000m);
        for (var i = 0; i < 4; i++)
            await rig.Buy(0.0001m, 59_950m, $"c-{i}");

        Assert.Equal("order-rate", (await rig.Buy(0.0001m, 59_950m, "c-4")).Rule);

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Quote(60_000.00m, 60_010.00m);
        Assert.Equal(ExecutionStatus.Placed, (await rig.Buy(0.0001m, 59_950m, "c-5")).Status);
    }

    [Fact]
    public async Task OrdersNotListedYet_StillCountTowardThePositionCap()
    {
        var rig = new Rig(eur: 1_000m);
        rig.Exchange.HideOpenOrders = true; // the venue hasn't listed them yet
        await rig.Buy(0.0006m, 59_950m, "a");
        await rig.Buy(0.0006m, 59_950m, "b");

        var withoutOverlay = await rig.Snapshot();
        var result = await rig.Buy(0.0006m, 59_950m, "c");

        Assert.Equal(0m, withoutOverlay.TotalExposureQuote);
        Assert.Equal("position-cap", result.Rule);
        Assert.Equal(0.00046805m, result.Order!.BaseSize);
    }

    [Fact]
    public async Task PendingOrderThatWasCancelled_StopsCounting()
    {
        var rig = new Rig(eur: 1_000m);
        rig.Exchange.HideOpenOrders = true;
        var placed = await rig.Buy(0.0006m, 59_950m, "a");
        await rig.Paper.CancelOrderAsync(placed.Placed!.VenueOrderId, CancellationToken.None);

        var result = await rig.Buy(0.0006m, 59_950m, "b");

        Assert.Equal(("ok", 0.0006m), (result.Rule, result.Order!.BaseSize));
        var snapshot = await rig.Portfolio.GetSnapshotAsync(0, [], CancellationToken.None);
        Assert.Equal(0m, snapshot.TotalExposureQuote);
    }

    [Fact]
    public async Task ReusedClientOrderId_IsRejected_UnlessTheExchangeRefusedTheFirst()
    {
        var rig = new Rig(eur: 40m);
        await rig.Buy(0.0003m, 59_950m, "same"); // 17.99 EUR reserved

        Assert.Equal("duplicate-client-order-id", (await rig.Buy(0.0003m, 59_950m, "same")).Rule);

        var refused = await rig.Buy(0.0005m, 59_950m, "retry"); // 29.98 EUR > 22.01 left: the exchange refuses
        var retried = await rig.Buy(0.0003m, 59_950m, "retry");
        Assert.Equal(ExecutionStatus.Failed, refused.Status);
        Assert.Equal(ExecutionStatus.Placed, retried.Status);
    }

    [Fact]
    public async Task Approval_SendsAtMostOneOrder()
    {
        var rig = new Rig();
        var placed = await rig.Buy(0.0005m, 59_950m, "once");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Paper.PlaceLimitOrderAsync(placed.Order!, false, CancellationToken.None));
    }

    [Fact]
    public async Task PairRulesUnavailable_RejectsInsteadOfThrowing()
    {
        var rig = new Rig(pairSource: new FailingPairs());

        Assert.Equal("pairs-unavailable", (await rig.Buy(0.0005m, 59_950m)).Rule);
    }

    private sealed class FailingPairs : IMarketDataSource
    {
        public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused.");

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candle>>([]);
    }

    [Fact]
    public async Task UncertainPlacement_FoundByClientOrderId_IsReportedPlacedNotResent()
    {
        var rig = new Rig();
        rig.Exchange.UncertainAfterSending = true;

        var result = await rig.Buy(0.0005m, 59_950m);

        Assert.Equal(ExecutionStatus.Placed, result.Status);
        Assert.Equal(result.Placed!.VenueOrderId, Assert.Single(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).VenueOrderId);
        Assert.Empty(rig.Execution.Unresolved);
    }

    [Fact]
    public async Task UncertainPlacement_NotFound_IsHeld_AndBlocksResendingTheSameId()
    {
        var rig = new Rig();
        rig.Exchange.UncertainAfterSending = false; // it never arrived, but we can't know that

        var first = await rig.Buy(0.0005m, 59_950m, "u-1");
        rig.Exchange.UncertainAfterSending = null;
        var resend = await rig.Buy(0.0005m, 59_950m, "u-1");

        Assert.Equal(ExecutionStatus.Uncertain, first.Status);
        Assert.Equal(1 + 3, rig.Exchange.OpenOrderLookups); // the portfolio's read, then three lookups; the resend sends nothing
        Assert.Equal("placement-unresolved", resend.Rule);
        Assert.Single(rig.Execution.Unresolved);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task UncertainBuy_CountsAsExposureUntilReconciled()
    {
        var rig = new Rig(eur: 1_000m);
        rig.Exchange.UncertainAfterSending = false;
        await rig.Buy(0.0006m, 59_950m, "u-1");
        rig.Exchange.UncertainAfterSending = null;

        rig.Time.Advance(TimeSpan.FromMinutes(10)); // long after the pending window
        rig.Quote(60_000.00m, 60_010.00m);
        var snapshot = await rig.Portfolio.GetSnapshotAsync(0, rig.Execution.Unresolved, CancellationToken.None);

        Assert.Equal(35.97m, snapshot.TotalExposureQuote);
    }

    [Fact]
    public async Task KillSwitch_CancelsOpenOrders_AndBlocksNewOnes_UntilReleased()
    {
        var rig = new Rig(eur: 1_000m);
        await rig.Buy(0.0005m, 59_950m, "a");
        await rig.Buy(0.0005m, 59_900m, "b");

        var result = await rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);
        var blocked = await rig.Buy(0.0005m, 59_950m, "c");

        Assert.Equal((true, 2), (result.NewlyEngaged, result.Cancelled));
        Assert.Empty(result.NotCancelled);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal("kill-switch", blocked.Rule);
        Assert.Equal(("drill", "owner"), (rig.KillSwitch.State.Reason, rig.KillSwitch.State.By));

        rig.Execution.ReleaseKillSwitch("owner");
        Assert.Equal(ExecutionStatus.Placed, (await rig.Buy(0.0005m, 59_950m, "d")).Status);
    }

    [Fact]
    public async Task KillSwitch_CancelsOrdersTheExchangeHasNotListedYet_AndReportsUncertainOnes()
    {
        var rig = new Rig(eur: 1_000m);
        rig.Exchange.HideOpenOrders = true;
        await rig.Buy(0.0005m, 59_950m, "a");
        rig.Exchange.UncertainAfterSending = false;
        await rig.Buy(0.0005m, 59_900m, "u");

        var result = await rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);

        Assert.Equal(1, result.Cancelled);
        Assert.Equal(new[] { "uncertain:u" }, result.NotCancelled);
        Assert.Empty(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task KillSwitch_EngagedTwice_KeepsTheFirstReason()
    {
        var rig = new Rig();

        await rig.Execution.EngageKillSwitchAsync("first", "monitor", CancellationToken.None);
        var second = await rig.Execution.EngageKillSwitchAsync("second", "owner", CancellationToken.None);

        Assert.False(second.NewlyEngaged);
        Assert.Equal("first", rig.KillSwitch.State.Reason);
    }

    [Fact]
    public async Task HeldAssetWithoutFreshPrice_RejectsInsteadOfGuessing()
    {
        var rig = new Rig(btc: 0.001m);
        rig.Time.Advance(TimeSpan.FromMinutes(6)); // the last quote is now too old to value the BTC

        var result = await rig.Buy(0.0001m, 59_950m);

        Assert.Equal("portfolio-unavailable", result.Rule);
    }

    [Fact]
    public async Task Portfolio_TracksPeakAndDailyBaselineFromBalancesAndPrices()
    {
        var rig = new Rig(eur: 200m, btc: 0.001m);

        var start = await rig.Snapshot();
        rig.Quote(50_000.00m, 50_010.00m);
        var dropped = await rig.Snapshot();
        rig.Time.Advance(TimeSpan.FromDays(1));
        rig.Quote(50_000.00m, 50_010.00m);
        var nextDay = await rig.Snapshot();

        Assert.Equal((260.005m, 260.005m, 0m, 60.005m), (start.Equity, start.PeakEquity, start.PnlToday, start.TotalExposureQuote));
        Assert.Equal((250.005m, 260.005m, -10m), (dropped.Equity, dropped.PeakEquity, dropped.PnlToday));
        Assert.Equal((250.005m, 260.005m, 0m), (nextDay.Equity, nextDay.PeakEquity, nextDay.PnlToday));
    }

    [Fact]
    public async Task DailyLossFromAPriceDrop_HaltsBuys_ButAllowsSelling()
    {
        var rig = new Rig(eur: 100m, btc: 0.002m);
        await rig.Snapshot(); // equity 220.01 at the day's start
        rig.Quote(51_000.00m, 51_010.00m); // BTC down 15%: equity 202.01, -18 EUR on the day

        var buy = await rig.Buy(0.0001m, 51_000m);
        var sell = await rig.Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Sell, 0.001m, 51_010m, "s-1"), false, CancellationToken.None);

        Assert.Equal("daily-loss", buy.Rule);
        Assert.Equal(ExecutionStatus.Placed, sell.Status);
    }

    [Fact]
    public void RoundToSteps_SellPricesRoundUp_SizesRoundDown()
    {
        var pair = new PairInfo(Btc, "BTC", "EUR", 0.0001m, 0.01m, 0.0001m, 200m, 0.1m, true);

        var rounded = ExecutionService.RoundToSteps(new OrderIntent(Btc, OrderSide.Sell, 0.00129m, 60_000.001m, "s"), pair);

        Assert.Equal((0.0012m, 60_000.01m), (rounded.BaseSize, rounded.LimitPrice));
    }
}

public sealed class EngineStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cix-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void KillSwitchAndBaselines_SurviveARestart()
    {
        var path = Path.Combine(_dir, "state.json");
        var time = new MutableTime(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        var first = new FileEngineStateStore(path, time);
        first.Update(s => s with
        {
            KillSwitch = new KillSwitchState(true, "drill", "owner", time.GetUtcNow()),
            PeakEquity = 250m,
            Day = new DateOnly(2026, 10, 22),
            DayStartEquity = 240m,
        });

        var restarted = new FileEngineStateStore(path, time);

        Assert.True(new KillSwitch(restarted, time).IsEngaged);
        Assert.Equal(first.Current, restarted.Current);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UnreadableStateFile_StopsStartup()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{ not json");

        Assert.Throws<InvalidOperationException>(() => new FileEngineStateStore(path, TimeProvider.System));
    }

    [Fact]
    public void StateFileWithoutKillSwitch_StopsStartup()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{}");

        Assert.Throws<InvalidOperationException>(() => new FileEngineStateStore(path, TimeProvider.System));
    }

    [Fact]
    public void MissingStateFile_StartsEngaged_AndWritesTheFile()
    {
        var path = Path.Combine(_dir, "none.json");

        var store = new FileEngineStateStore(path, TimeProvider.System);

        Assert.True(store.Current.KillSwitch.Engaged);
        Assert.Equal(FileEngineStateStore.NewFileReason, store.Current.KillSwitch.Reason);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RelativeStatePath_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new FileEngineStateStore("state.json", TimeProvider.System));
}
