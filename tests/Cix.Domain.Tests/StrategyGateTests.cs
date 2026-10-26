using Cix.Domain.Risk;

namespace Cix.Domain.Tests;

public sealed class StrategyGateTests
{
    private const string Btc = "BTC-EUR";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly StrategyActivity Idle = new(null, 0);

    private static PortfolioSnapshot Portfolio(decimal position = 0m, decimal exposure = 0m, decimal pnlToday = 0m, decimal openBuys = 0m) =>
        new(200m, 200m, pnlToday, exposure, new Dictionary<string, decimal> { [Btc] = position }, 0, false,
            new Dictionary<string, decimal> { [Btc] = openBuys });

    /// <summary>A buy worth <paramref name="quote"/> at 50,000.</summary>
    private static OrderIntent Buy(decimal quote) => new(Btc, OrderSide.Buy, quote / 50_000m, 50_000m, "c-1");

    private static StrategyVerdict Check(OrderIntent intent, StrategyLimits limits, PortfolioSnapshot? portfolio = null, StrategyActivity? activity = null) =>
        StrategyGate.CheckBuy(intent, portfolio ?? Portfolio(), limits, activity ?? Idle, Now, 0.00000001m);

    [Fact]
    public void Sells_AlwaysPass_WhateverTheLimits()
    {
        var sell = new OrderIntent(Btc, OrderSide.Sell, 1m, 50_000m, "c-1");
        var limits = new StrategyLimits(OrderQuote: 1m, MaxExposureQuote: 0m, DailyLossStopQuote: 1m, MaxNewPositionsPerRun: 0, EntryCooldown: TimeSpan.FromDays(1));

        var verdict = Check(sell, limits, Portfolio(pnlToday: -100m), new StrategyActivity(Now, 9));

        Assert.Same(sell, verdict.Order);
        Assert.False(verdict.Resized);
    }

    [Fact]
    public void NoLimits_LeaveTheBuyAsItIs()
    {
        var buy = Buy(35m);

        var verdict = Check(buy, StrategyLimits.None);

        Assert.Equal((buy, false, "ok"), (verdict.Order, verdict.Resized, verdict.Rule));
    }

    [Fact]
    public void DailyLossStop_RefusesBuys_OnceTodaysLossReachesIt()
    {
        var limits = new StrategyLimits(DailyLossStopQuote: 5m);

        Assert.NotNull(Check(Buy(5m), limits, Portfolio(pnlToday: -4.99m)).Order);
        var verdict = Check(Buy(5m), limits, Portfolio(pnlToday: -5m));

        Assert.Equal((null, "strategy-daily-loss-stop"), (verdict.Order, verdict.Rule));
    }

    [Fact]
    public void PositionsPerRun_RefusesTheBuyBeyondTheLimit()
    {
        var limits = new StrategyLimits(MaxNewPositionsPerRun: 2);

        Assert.NotNull(Check(Buy(5m), limits, activity: new StrategyActivity(null, 1)).Order);
        Assert.Equal("strategy-positions-per-run", Check(Buy(5m), limits, activity: new StrategyActivity(null, 2)).Rule);
        Assert.Null(Check(Buy(5m), new StrategyLimits(MaxNewPositionsPerRun: 0)).Order);
    }

    [Fact]
    public void Cooldown_RefusesBuysUntilItHasPassedSinceTheLastOne()
    {
        var limits = new StrategyLimits(EntryCooldown: TimeSpan.FromMinutes(60));

        var early = Check(Buy(5m), limits, activity: new StrategyActivity(Now.AddMinutes(-59), 0));
        var later = Check(Buy(5m), limits, activity: new StrategyActivity(Now.AddMinutes(-60), 0));

        Assert.Equal((null, "strategy-cooldown"), (early.Order, early.Rule));
        Assert.NotNull(later.Order);
        Assert.NotNull(Check(Buy(5m), limits).Order); // no buy yet: nothing to wait for
    }

    [Fact]
    public void OrderSize_AllowsTenPercentForRounding_AndReducesALargerBuyToTheOrderSize()
    {
        var limits = new StrategyLimits(OrderQuote: 10m);

        var rounded = Check(Buy(11m), limits);
        var large = Check(Buy(30m), limits);

        Assert.Equal((false, "ok"), (rounded.Resized, rounded.Rule));
        Assert.Equal((true, "strategy-order-size"), (large.Resized, large.Rule));
        Assert.Equal(0.0002m, large.Order!.BaseSize); // 10 / 50,000
    }

    [Fact]
    public void PositionCap_CountsHoldingsAndWorkingBuys_AndReducesOrRefuses()
    {
        var limits = new StrategyLimits(MaxPositionQuotePerSymbol: 25m);

        var reduced = Check(Buy(10m), limits, Portfolio(position: 15m, openBuys: 5m));
        var refused = Check(Buy(10m), limits, Portfolio(position: 20m, openBuys: 5m));

        Assert.Equal((true, "strategy-position-cap", 0.0001m), (reduced.Resized, reduced.Rule, reduced.Order!.BaseSize)); // 5 left
        Assert.Equal((null, "strategy-position-cap"), (refused.Order, refused.Rule));
    }

    [Fact]
    public void ExposureCap_IsTotalAcrossCoins_AndTheTightestLimitDecides()
    {
        var limits = new StrategyLimits(OrderQuote: 20m, MaxPositionQuotePerSymbol: 50m, MaxExposureQuote: 100m);

        var verdict = Check(Buy(40m), limits, Portfolio(position: 0m, exposure: 92m));

        Assert.Equal((true, "strategy-exposure-cap"), (verdict.Resized, verdict.Rule)); // 8 left, under the 20 order size
        Assert.Equal(0.00016m, verdict.Order!.BaseSize);
        Assert.Equal("strategy-exposure-cap", Check(Buy(5m), limits, Portfolio(exposure: 100m)).Rule);
    }

    [Fact]
    public void RoomTooSmallForOneStep_IsRefused()
    {
        var verdict = StrategyGate.CheckBuy(Buy(10m), Portfolio(exposure: 99.99m), new StrategyLimits(MaxExposureQuote: 100m), Idle, Now, 0.001m);

        Assert.Equal((null, "strategy-exposure-cap"), (verdict.Order, verdict.Rule));
    }
}
