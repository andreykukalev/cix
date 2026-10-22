using System.Globalization;
using System.Reflection;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;

namespace Cix.Domain.Tests;

public sealed class RiskGateTests
{
    private const string Btc = "BTC-EUR";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static RiskLimits Limits() => new(
        AllocationQuote: 200m,
        MaxOrderQuote: 40m,
        MaxPositionQuotePerSymbol: 100m,
        MaxDailyLossQuote: 15m,
        MaxDrawdownFraction: 0.15m,
        MaxOrdersPerMinute: 4,
        MaxPriceDeviationFraction: 0.01m,
        AllowedSymbols: new HashSet<string> { Btc });

    private static RiskGate Gate(RiskLimits? limits = null) => new(limits ?? Limits(), new FixedTime(Now));

    private static PortfolioSnapshot Portfolio(
        decimal equity = 200m,
        decimal peak = 200m,
        decimal pnlToday = 0m,
        decimal exposure = 0m,
        decimal btcPosition = 0m,
        int ordersLastMinute = 0,
        bool kill = false) =>
        new(equity, peak, pnlToday, exposure, new Dictionary<string, decimal> { [Btc] = btcPosition }, ordersLastMinute, kill);

    private static MarketSnapshot Market(decimal price = 50_000m, DateTimeOffset? asOf = null) => new(Btc, price, asOf ?? Now);

    private static OrderIntent Buy(decimal size, decimal price = 50_000m) => new(Btc, OrderSide.Buy, size, price, "c-1");

    private static OrderIntent Sell(decimal size, decimal price = 50_000m) => new(Btc, OrderSide.Sell, size, price, "c-2");

    [Fact]
    public void Evaluate_OrderWithinLimits_Accepts()
    {
        var verdict = Gate().Evaluate(Buy(0.0006m), Portfolio(), Market()); // 30 EUR

        Assert.Equal(RiskOutcome.Accepted, verdict.Outcome);
        Assert.Equal(0.0006m, verdict.Order!.BaseSize);
    }

    [Fact]
    public void Evaluate_KillSwitchEngaged_RejectsEvenSells()
    {
        var verdict = Gate().Evaluate(Sell(0.0001m), Portfolio(btcPosition: 50m, kill: true), Market());

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.Equal("kill-switch", verdict.Rule);
    }

    [Fact]
    public void Evaluate_SymbolNotAllowed_Rejects()
    {
        var order = new OrderIntent("DOGE-EUR", OrderSide.Buy, 10m, 0.2m, "c-3");
        var verdict = Gate().Evaluate(order, Portfolio(), new MarketSnapshot("DOGE-EUR", 0.2m, Now));

        Assert.Equal("symbol-allowlist", verdict.Rule);
    }

    [Fact]
    public void Evaluate_StaleMarketData_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0006m), Portfolio(), Market(asOf: Now.AddMinutes(-6)));

        Assert.Equal("stale-market-data", verdict.Rule);
    }

    [Fact]
    public void Evaluate_PriceOutsideBand_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0006m, price: 50_600m), Portfolio(), Market()); // 1.2% away

        Assert.Equal("price-band", verdict.Rule);
    }

    [Fact]
    public void Evaluate_OrderRateReached_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0006m), Portfolio(ordersLastMinute: 4), Market());

        Assert.Equal("order-rate", verdict.Rule);
    }

    [Fact]
    public void Evaluate_BuyAboveMaxOrder_ResizesToMaxOrder()
    {
        var verdict = Gate().Evaluate(Buy(0.002m), Portfolio(), Market()); // 100 EUR asked, 40 allowed

        Assert.Equal(RiskOutcome.Resized, verdict.Outcome);
        Assert.Equal("max-order", verdict.Rule);
        Assert.Equal(0.0008m, verdict.Order!.BaseSize);
    }

    [Fact]
    public void Evaluate_BuyNearAllocation_ResizesToRemainingAllocation()
    {
        var verdict = Gate().Evaluate(Buy(0.0008m), Portfolio(exposure: 190m), Market()); // 40 asked, 10 left

        Assert.Equal(RiskOutcome.Resized, verdict.Outcome);
        Assert.Equal("allocation-cap", verdict.Rule);
        Assert.Equal(0.0002m, verdict.Order!.BaseSize);
    }

    [Fact]
    public void Evaluate_AllocationFullyUsed_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0002m), Portfolio(exposure: 200m), Market());

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.Equal("allocation-cap", verdict.Rule);
    }

    [Fact]
    public void Evaluate_DrawdownAtLimit_RejectsBuys()
    {
        var verdict = Gate().Evaluate(Buy(0.0002m), Portfolio(equity: 170m, peak: 200m), Market()); // 15%

        Assert.Equal("drawdown-halt", verdict.Rule);
    }

    [Fact]
    public void Evaluate_DrawdownAtLimit_StillAllowsRiskReducingSell()
    {
        var verdict = Gate().Evaluate(Sell(0.0002m), Portfolio(equity: 170m, peak: 200m, btcPosition: 50m), Market());

        Assert.Equal(RiskOutcome.Accepted, verdict.Outcome);
    }

    [Fact]
    public void Evaluate_DailyLossReached_RejectsBuys()
    {
        var verdict = Gate().Evaluate(Buy(0.0002m), Portfolio(pnlToday: -15m), Market());

        Assert.Equal("daily-loss", verdict.Rule);
    }

    [Fact]
    public void Evaluate_SellWithoutPosition_Rejects()
    {
        var verdict = Gate().Evaluate(Sell(0.0002m), Portfolio(), Market());

        Assert.Equal("no-position", verdict.Rule);
    }

    [Fact]
    public void Evaluate_SellLargerThanPosition_ResizesToPosition()
    {
        var verdict = Gate().Evaluate(Sell(0.001m), Portfolio(btcPosition: 25m), Market()); // 50 asked, 25 held

        Assert.Equal(RiskOutcome.Resized, verdict.Outcome);
        Assert.Equal(0.0005m, verdict.Order!.BaseSize);
    }

    private static PairInfo Pair(decimal baseStep = 0.00000001m, decimal quoteStep = 0.01m, decimal minQuote = 0.1m, decimal maxSize = 200m, bool active = true) =>
        new(Btc, "BTC", "EUR", baseStep, quoteStep, baseStep, maxSize, minQuote, active);

    [Fact]
    public void Evaluate_WithPair_ResizeRoundsDownToTheBaseStep()
    {
        var verdict = Gate().Evaluate(Buy(0.002m), Portfolio(), Market(), Pair(baseStep: 0.0005m)); // 40 EUR allows 0.0008

        Assert.Equal(RiskOutcome.Resized, verdict.Outcome);
        Assert.Equal(0.0005m, verdict.Order!.BaseSize);
    }

    [Theory]
    [InlineData("0.00061", "50000", "step")]       // size off a 0.0001 step
    [InlineData("0.0006", "50000.005", "step")]    // price off a 0.01 step
    public void Evaluate_OffStep_Rejects(string size, string price, string rule)
    {
        var order = Buy(decimal.Parse(size, CultureInfo.InvariantCulture), decimal.Parse(price, CultureInfo.InvariantCulture));

        var verdict = Gate().Evaluate(order, Portfolio(), Market(), Pair(baseStep: 0.0001m));

        Assert.Equal(rule, verdict.Rule);
        Assert.Null(verdict.Order);
    }

    [Fact]
    public void Evaluate_InactivePair_Rejects() =>
        Assert.Equal("pair-inactive", Gate().Evaluate(Buy(0.0006m), Portfolio(), Market(), Pair(active: false)).Rule);

    [Fact]
    public void Evaluate_BelowExchangeMinimum_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0001m), Portfolio(), Market(), Pair(minQuote: 10m)); // 5 EUR

        Assert.Equal("exchange-min-size", verdict.Rule);
    }

    [Fact]
    public void Evaluate_ResizedBelowExchangeMinimum_Rejects()
    {
        var verdict = Gate().Evaluate(Buy(0.0008m), Portfolio(exposure: 195m), Market(), Pair(minQuote: 10m)); // 5 EUR left

        Assert.Equal("exchange-min-size", verdict.Rule);
        Assert.Contains("allocation-cap", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AboveExchangeMaximum_ResizesToIt()
    {
        var verdict = Gate().Evaluate(Buy(0.0008m), Portfolio(), Market(), Pair(maxSize: 0.0005m));

        Assert.Equal("exchange-max-size", verdict.Rule);
        Assert.Equal(0.0005m, verdict.Order!.BaseSize);
    }

    [Fact]
    public void Evaluate_MarketDataFromTheFuture_Rejects() =>
        Assert.Equal("stale-market-data", Gate().Evaluate(Buy(0.0006m), Portfolio(), Market(asOf: Now.AddMinutes(1))).Rule);

    [Fact]
    public void Evaluate_MissingClientOrderId_Rejects() =>
        Assert.Equal("invalid-order", Gate().Evaluate(Buy(0.0006m) with { ClientOrderId = " " }, Portfolio(), Market()).Rule);

    [Fact]
    public void Evaluate_Approval_CarriesTheApprovedValuesAndTime()
    {
        var order = Gate().Evaluate(Buy(0.0006m), Portfolio(), Market()).Order!;

        Assert.Equal((Btc, OrderSide.Buy, 0.0006m, 50_000m, "c-1", Now), (order.Symbol, order.Side, order.BaseSize, order.LimitPrice, order.ClientOrderId, order.ApprovedAt));
        Assert.Equal(new OrderIntent(Btc, OrderSide.Buy, 0.0006m, 50_000m, "c-1"), order.ToIntent());
    }

    [Fact]
    public void ApprovedOrder_CanOnlyBeCreatedByTheGate()
    {
        var type = typeof(ApprovedOrder);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance), c => c.IsFamily || c.IsFamilyOrAssembly);
        Assert.Null(type.GetMethod("<Clone>$")); // not a record: no `with` copies
        Assert.DoesNotContain(
            typeof(ApprovedOrder).Assembly.GetExportedTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            m => m.ReturnType == typeof(ApprovedOrder) || m.ReturnType == typeof(RiskVerdict) && m.Name != nameof(RiskVerdict.Reject));
    }

    [Fact]
    public void ExchangeClients_AcceptOnlyApprovedOrders()
    {
        var place = typeof(IExchangeClient).GetMethod(nameof(IExchangeClient.PlaceLimitOrderAsync))!;

        Assert.Equal(typeof(ApprovedOrder), place.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Constructor_LimitsAboveHardCeilings_Throws()
    {
        var tooLoose = Limits() with { MaxDrawdownFraction = HardCeilings.MaxDrawdownFraction + 0.01m };

        Assert.Throws<ArgumentException>(() => new RiskGate(tooLoose));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
