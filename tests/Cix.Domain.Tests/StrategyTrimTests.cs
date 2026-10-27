using Cix.Domain.Exchange;
using Cix.Domain.Risk;

namespace Cix.Domain.Tests;

public sealed class StrategyTrimTests
{
    private static PairInfo Pair(string symbol, decimal minQuote = 1m) =>
        new(symbol, symbol.Split('-')[0], "EUR", 0.00000001m, 0.01m, 0.00000001m, 1_000m, minQuote, true);

    /// <summary>A holding worth <paramref name="value"/> at <paramref name="bid"/>, all of it sellable.</summary>
    private static TrimHolding Held(string symbol, decimal value, decimal bid, decimal minQuote = 1m) =>
        new(symbol, value, value / bid, bid, Pair(symbol, minQuote));

    private static readonly TrimHolding Btc40 = Held("BTC-EUR", 40m, 50_000m);
    private static readonly TrimHolding Eth20 = Held("ETH-EUR", 20m, 2_000m);

    [Fact]
    public void WithoutCaps_OrWithinThem_NothingIsSold()
    {
        Assert.Empty(StrategyTrim.Plan([Btc40, Eth20], StrategyLimits.None));
        Assert.Empty(StrategyTrim.Plan([Btc40, Eth20], new StrategyLimits(OrderQuote: 5m, MaxPositionQuotePerSymbol: 50m, MaxExposureQuote: 60m)));
    }

    [Fact]
    public void APerCoinCap_SellsOnlyWhatIsAboveIt_AtTheBid()
    {
        var sell = Assert.Single(StrategyTrim.Plan([Btc40, Eth20], new StrategyLimits(MaxPositionQuotePerSymbol: 25m)));

        Assert.Equal(("BTC-EUR", 0.0003m, 50_000m, "strategy-position-cap"), (sell.Symbol, sell.BaseSize, sell.LimitPrice, sell.Rule)); // 15 / 50,000
    }

    [Fact]
    public void AnExposureCap_TrimsTheLargestHoldingsFirst()
    {
        var sell = Assert.Single(StrategyTrim.Plan([Eth20, Btc40], new StrategyLimits(MaxExposureQuote: 35m)));

        Assert.Equal(("BTC-EUR", 0.0005m, "strategy-exposure-cap"), (sell.Symbol, sell.BaseSize, sell.Rule)); // 60 - 35 = 25, all from BTC
    }

    [Fact]
    public void AnExposureCap_MovesOnToTheNextHolding_OnceTheLargestIsUsedUp()
    {
        var sells = StrategyTrim.Plan([Btc40, Eth20], new StrategyLimits(MaxExposureQuote: 10m));

        Assert.Equal(new[] { ("BTC-EUR", 0.0008m), ("ETH-EUR", 0.005m) }, sells.Select(s => (s.Symbol, s.BaseSize))); // all 40 of BTC, 10 of ETH
    }

    [Fact]
    public void BothCaps_TheExposureCapCountsWhatThePerCoinCapAlreadySells()
    {
        // Per coin: BTC 40 -> 25 (sells 15). Then 25 + 20 = 45 against 30: 15 more, from BTC (the larger at 25).
        var sell = Assert.Single(StrategyTrim.Plan([Btc40, Eth20], new StrategyLimits(MaxPositionQuotePerSymbol: 25m, MaxExposureQuote: 30m)));

        Assert.Equal(("BTC-EUR", 0.0006m, "strategy-position-cap"), (sell.Symbol, sell.BaseSize, sell.Rule));
    }

    [Fact]
    public void ASellBelowTheExchangeMinimum_IsLeft_ButARemainderTooSmallToSellGoesToo()
    {
        Assert.Empty(StrategyTrim.Plan([Held("BTC-EUR", 12m, 50_000m, minQuote: 5m)], new StrategyLimits(MaxPositionQuotePerSymbol: 8m))); // 4 < 5

        var all = Assert.Single(StrategyTrim.Plan([Held("BTC-EUR", 12m, 50_000m, minQuote: 5m)], new StrategyLimits(MaxPositionQuotePerSymbol: 9m - 6m)));
        Assert.Equal(0.00024m, all.BaseSize); // 9 above the cap, and the 3 left couldn't be sold on its own
    }

    [Fact]
    public void OnlyTheSellableAmountIsSold_AndUnpricedHoldingsAreLeft()
    {
        var partlySellable = new TrimHolding("BTC-EUR", 40m, 0.0002m, 50_000m, Pair("BTC-EUR"));

        var sell = Assert.Single(StrategyTrim.Plan([partlySellable, Held("ETH-EUR", 20m, 2_000m) with { Bid = 0m }], new StrategyLimits(MaxPositionQuotePerSymbol: 10m)));
        Assert.Equal(("BTC-EUR", 0.0002m), (sell.Symbol, sell.BaseSize));
    }
}
