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
        AllowedQuoteCurrency: "EUR",
        MaxTradableSymbols: 3);

    /// <summary>A gate whose watched list is BTC-EUR alone.</summary>
    private static RiskGate Gate(RiskLimits? limits = null)
    {
        var l = limits ?? Limits();
        return new(l, new FixedTime(Now), new TradableSymbols(l, [Btc]));
    }

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

    private static OrderIntent StopSell(decimal size, decimal price = 49_600m) =>
        new(Btc, OrderSide.Sell, size, price, "c-stop", OrderPurpose.ProtectiveExit, TimeInForce.ImmediateOrCancel);

    private static RiskLimits WithEmergencyBand(decimal? band) => Limits() with { MaxEmergencySlippageFraction = band };

    [Fact]
    public void Evaluate_KillSwitchEngaged_LetsTheEnginesStopLossSellThrough()
    {
        var verdict = Gate().Evaluate(StopSell(0.0001m), Portfolio(btcPosition: 50m, kill: true), Market());

        Assert.Equal(RiskOutcome.Accepted, verdict.Outcome);
        Assert.Equal((OrderPurpose.ProtectiveExit, TimeInForce.ImmediateOrCancel), (verdict.Order!.Purpose, verdict.Order.TimeInForce));
    }

    [Fact]
    public void Evaluate_KillSwitchEngaged_LetsTheOwnersOwnSellThrough_SizedToThePosition_ButNoBuy()
    {
        var sell = Sell(0.002m) with { Purpose = OrderPurpose.OwnerSell }; // 100 EUR asked, 50 EUR held

        var verdict = Gate().Evaluate(sell, Portfolio(btcPosition: 50m, kill: true), Market());

        Assert.Equal((RiskOutcome.Resized, "position-size", 0.001m, OrderPurpose.OwnerSell), (verdict.Outcome, verdict.Rule, verdict.Order!.BaseSize, verdict.Order.Purpose));
        Assert.Equal("no-position", Gate().Evaluate(sell, Portfolio(btcPosition: 0m, kill: true), Market()).Rule);
        Assert.Equal("price-band", Gate().Evaluate(sell with { LimitPrice = 49_000m }, Portfolio(btcPosition: 50m, kill: true), Market()).Rule); // the normal band
        Assert.Equal("invalid-order", Gate().Evaluate(Buy(0.0001m) with { Purpose = OrderPurpose.OwnerSell }, Portfolio(kill: true), Market()).Rule);
    }

    [Fact]
    public void Evaluate_ProtectiveExit_MustBeAnImmediateOrCancelSell()
    {
        var restingSell = StopSell(0.0001m) with { TimeInForce = TimeInForce.GoodTillCancelled };
        var buy = StopSell(0.0001m) with { Side = OrderSide.Buy };

        foreach (var order in new[] { restingSell, buy })
        {
            var verdict = Gate().Evaluate(order, Portfolio(btcPosition: 50m, kill: true), Market());
            Assert.Equal(("invalid-order", RiskOutcome.Rejected), (verdict.Rule, verdict.Outcome));
        }
    }

    [Fact]
    public void Evaluate_ProtectiveExit_StillNeedsAPosition_FreshData_AndTheAllowlist()
    {
        Assert.Equal("no-position", Gate().Evaluate(StopSell(0.0001m), Portfolio(btcPosition: 0m, kill: true), Market()).Rule);
        Assert.Equal("stale-market-data", Gate().Evaluate(StopSell(0.0001m), Portfolio(btcPosition: 50m), Market(asOf: Now.AddMinutes(-6))).Rule);
        var doge = new OrderIntent("DOGE-EUR", OrderSide.Sell, 10m, 0.2m, "c-d", OrderPurpose.ProtectiveExit, TimeInForce.ImmediateOrCancel);
        Assert.Equal("symbol-allowlist", Gate().Evaluate(doge, Portfolio(kill: true), new MarketSnapshot("DOGE-EUR", 0.2m, Now)).Rule);

        // Never more than the position: resized down like any sell.
        var verdict = Gate().Evaluate(StopSell(0.01m), Portfolio(btcPosition: 49.6m), Market());
        Assert.Equal(("position-size", 0.001m), (verdict.Rule, verdict.Order!.BaseSize));
    }

    [Fact]
    public void Evaluate_ProtectiveExit_UsesTheEmergencyBand_OrTheNormalOneWhenUnset()
    {
        // 3% below the mid: outside the normal 1% band, inside a 3% emergency band.
        var deep = StopSell(0.0001m, price: 48_500m);

        Assert.Equal("price-band", Gate().Evaluate(deep, Portfolio(btcPosition: 50m), Market()).Rule);
        Assert.Equal(RiskOutcome.Accepted, Gate(WithEmergencyBand(0.03m)).Evaluate(deep, Portfolio(btcPosition: 50m), Market()).Outcome);
        Assert.Equal("price-band", Gate(WithEmergencyBand(0.03m)).Evaluate(StopSell(0.0001m, price: 48_400m), Portfolio(btcPosition: 50m), Market()).Rule);

        // An ordinary sell keeps the normal band whatever the emergency one is.
        Assert.Equal("price-band", Gate(WithEmergencyBand(0.03m)).Evaluate(Sell(0.0001m, 48_500m), Portfolio(btcPosition: 50m), Market()).Rule);
    }

    [Fact]
    public void RiskLimits_EmergencyBand_IsCappedByTheHardCeilingOnPriceDeviation()
    {
        Assert.Empty(WithEmergencyBand(HardCeilings.MaxPriceDeviationFraction).Validate());
        Assert.Contains(WithEmergencyBand(HardCeilings.MaxPriceDeviationFraction + 0.0001m).Validate(), e => e.Contains("MaxEmergencySlippageFraction", StringComparison.Ordinal));
        Assert.Contains(WithEmergencyBand(0m).Validate(), e => e.Contains("MaxEmergencySlippageFraction", StringComparison.Ordinal));
        Assert.Equal(0.01m, Limits().EmergencySlippageFraction); // unset: the normal band
    }

    [Fact]
    public void Evaluate_ProtectiveExits_AreNotCountedAgainstTheTradingRate_ButHaveABackstop()
    {
        var full = Portfolio(btcPosition: 50m, ordersLastMinute: 4); // the configured 4 a minute are used up

        Assert.Equal("order-rate", Gate().Evaluate(Sell(0.0001m), full, Market()).Rule);
        Assert.Equal("order-rate", Gate().Evaluate(Buy(0.0001m), full, Market()).Rule);
        Assert.Equal(RiskOutcome.Accepted, Gate().Evaluate(StopSell(0.0001m), full, Market()).Outcome);
        Assert.Equal("order-rate", Gate().Evaluate(StopSell(0.0001m), Portfolio(btcPosition: 50m, ordersLastMinute: RiskGate.MaxProtectiveOrdersPerMinute), Market()).Rule);
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
        Assert.EndsWith("new buys are refused while it stays at or above the limit.", verdict.Reason, StringComparison.Ordinal); // not "until resumed" (F-10)
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
    public void Evaluate_OpenBuys_CountTowardThePositionCap()
    {
        var portfolio = Portfolio(btcPosition: 50m) with { OpenBuyQuoteBySymbol = new Dictionary<string, decimal> { [Btc] = 40m } };

        var verdict = Gate().Evaluate(Buy(0.0008m), portfolio, Market()); // 40 asked, 10 left of 100

        Assert.Equal("position-cap", verdict.Rule);
        Assert.Equal(0.0002m, verdict.Order!.BaseSize);
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

    private const string Sol = "SOL-EUR";

    private static OrderIntent BuySol(decimal size = 0.2m, decimal price = 150m) => new(Sol, OrderSide.Buy, size, price, "c-3");

    [Fact]
    public void Constructor_WithoutAWatchedList_TradesNothing()
    {
        var gate = new RiskGate(Limits(), new FixedTime(Now)); // never a fallback to a wider list

        Assert.Equal("symbol-allowlist", gate.Evaluate(Buy(0.0006m), Portfolio(), Market()).Rule);
        Assert.Equal("symbol-allowlist", gate.Evaluate(StopSell(0.0001m), Portfolio(btcPosition: 50m), Market()).Rule);
    }

    [Fact]
    public void Evaluate_PairAddedAtRunTime_Accepts()
    {
        var limits = Limits();
        var tradable = new TradableSymbols(limits);
        var gate = new RiskGate(limits, new FixedTime(Now), tradable);
        Assert.Equal("symbol-allowlist", gate.Evaluate(BuySol(), Portfolio(), new MarketSnapshot(Sol, 150m, Now)).Rule); // not added yet

        Assert.Empty(tradable.TrySet([Btc, Sol], Now));
        var verdict = gate.Evaluate(BuySol(), Portfolio(), new MarketSnapshot(Sol, 150m, Now)); // 30 EUR

        Assert.Equal(RiskOutcome.Accepted, verdict.Outcome);
    }

    [Fact]
    public void Evaluate_PairRemovedAtRunTime_Rejects()
    {
        var limits = Limits();
        var tradable = new TradableSymbols(limits, [Btc]);
        Assert.Empty(tradable.TrySet([Sol], Now));
        var gate = new RiskGate(limits, new FixedTime(Now), tradable);

        var verdict = gate.Evaluate(Buy(0.0006m), Portfolio(), Market());

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.Equal("symbol-allowlist", verdict.Rule);
    }

    [Fact]
    public void Evaluate_APairInAnotherQuoteCurrency_IsNeverTraded()
    {
        var limits = Limits();
        var tradable = new TradableSymbols(limits, [Btc]);

        Assert.NotEmpty(tradable.TrySet([Btc, "ETH-USD"], Now));
        Assert.Equal("symbol-allowlist", new RiskGate(limits, new FixedTime(Now), tradable)
            .Evaluate(new OrderIntent("ETH-USD", OrderSide.Buy, 0.01m, 3_000m, "c-u"), Portfolio(), new MarketSnapshot("ETH-USD", 3_000m, Now)).Rule);
    }

    [Fact]
    public async Task TradableSymbols_SignalAChange_OnlyWhenTheListChanges()
    {
        var tradable = new TradableSymbols(Limits(), [Btc]);
        var wait = tradable.WaitForChangeAsync(tradable.Version, CancellationToken.None);
        Assert.False(wait.IsCompleted);

        Assert.Empty(tradable.TrySet([Btc], Now)); // the same list: nothing to rescan
        Assert.False(wait.IsCompleted);
        Assert.NotEmpty(tradable.TrySet([Btc, "ETH-USD"], Now)); // refused: nothing changed
        Assert.False(wait.IsCompleted);

        Assert.Empty(tradable.TrySet([Btc, Sol], Now));
        await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, tradable.Version);
        Assert.True(tradable.WaitForChangeAsync(0, CancellationToken.None).IsCompleted); // a change made while no one waited
    }

    [Fact]
    public void Constructor_ListForOtherLimits_Throws()
    {
        var tradable = new TradableSymbols(Limits());

        Assert.Throws<ArgumentException>(() => new RiskGate(Limits() with { MaxTradableSymbols = 4 }, new FixedTime(Now), tradable));
    }

    [Theory]
    [InlineData("BTC-EUR", true)]
    [InlineData("SOL-EUR", true)]
    [InlineData("ETH-USD", false)]
    [InlineData("sol-eur", false)]
    [InlineData("SOL-EUR-X", false)]
    [InlineData("SOLEUR", false)]
    [InlineData("-EUR", false)]
    public void Permits_AnyPairInTheQuoteCurrency_AndNothingElse(string symbol, bool permitted)
    {
        Assert.Equal(permitted, Limits().Permits(symbol));
    }

    [Fact]
    public void Validate_TheQuoteCurrencyAndTheCap_AreRequired()
    {
        Assert.Empty(Limits().Validate());
        Assert.Empty((Limits() with { MaxTradableSymbols = RiskLimits.MaxTradableSymbolsLimit }).Validate());
        Assert.Equal(50, RiskLimits.MaxTradableSymbolsLimit); // the owner's cap (2026-10-28): what the market-data budget fits
        Assert.NotEmpty((Limits() with { AllowedQuoteCurrency = "eur" }).Validate());
        Assert.NotEmpty((Limits() with { AllowedQuoteCurrency = "" }).Validate());
        Assert.NotEmpty((Limits() with { AllowedQuoteCurrency = null! }).Validate());
        Assert.NotEmpty((Limits() with { MaxTradableSymbols = 0 }).Validate());
        Assert.NotEmpty((Limits() with { MaxTradableSymbols = RiskLimits.MaxTradableSymbolsLimit + 1 }).Validate());
    }

    [Fact]
    public void TradableSymbols_OnlyListsTheLimitsPermit_AndMayBeEmpty()
    {
        var tradable = new TradableSymbols(Limits());
        Assert.Empty(tradable.Current); // a new environment watches nothing until the owner adds a coin

        Assert.Contains("At most 3 pairs can be tradable at once (the owner's limits file).", tradable.TrySet([Btc, Sol, "XRP-EUR", "ADA-EUR"], Now));
        // Paper's and live's limits files differ, so the reason names neither.
        Assert.Contains("Not permitted by the owner's limits file: ETH-USD.", tradable.TrySet([Btc, "ETH-USD"], Now));
        Assert.Empty(tradable.Current); // a refused list changes nothing

        Assert.Empty(tradable.TrySet([Btc], Now.AddMinutes(-1)));
        Assert.Empty(tradable.TrySet([Btc, Sol], Now));
        Assert.Equal(Now, tradable.AddedAt(Sol));
        Assert.Equal(Now.AddMinutes(-1), tradable.AddedAt(Btc)); // kept for a pair already there
        Assert.Null(tradable.AddedAt("XRP-EUR"));

        Assert.Empty(tradable.TrySet([], Now)); // the last coin can go too
        Assert.Empty(tradable.Current);
        Assert.Throws<ArgumentException>(() => new TradableSymbols(Limits(), ["ETH-USD"])); // nor can a list start outside the limits
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
