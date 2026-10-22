using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Engine.Paper;

namespace Cix.Engine.Tests;

public sealed class PaperExchangeTests
{
    private const string Btc = "BTC-EUR";
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly MutableTime _time = new(T0);

    private PaperExchange Create(decimal eur = 200m, decimal btc = 0m)
    {
        var options = new PaperOptions();
        options.InitialBalances["EUR"] = eur;
        options.InitialBalances["BTC"] = btc;
        var paper = new PaperExchange(new FakeMarketData(), options, _time);
        paper.OnQuote(Quote(bid: 60_000.00m, bidSize: 0.01m, ask: 60_010.00m, askSize: 0.002m));
        return paper;
    }

    private MarketQuote Quote(decimal bid, decimal bidSize, decimal ask, decimal askSize) =>
        new(Btc, bid, bidSize, ask, askSize, _time.GetUtcNow());

    private static OrderIntent Buy(decimal size, decimal price, string id = "c-1") => new(Btc, OrderSide.Buy, size, price, id);

    private static OrderIntent Sell(decimal size, decimal price, string id = "s-1") => new(Btc, OrderSide.Sell, size, price, id);

    private static async Task<Balance?> BalanceOf(PaperExchange paper, string currency) =>
        (await paper.GetBalancesAsync(CancellationToken.None)).SingleOrDefault(b => b.Currency == currency);

    private static async Task<ExchangeOrder> OrderOf(PaperExchange paper, PlacedOrder placed) =>
        (await paper.GetOrderAsync(placed.VenueOrderId, CancellationToken.None))!;

    [Fact]
    public async Task BuyBelowAsk_Rests_AndReservesFundsIncludingWorstCaseFee()
    {
        var paper = Create();

        var placed = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: false, CancellationToken.None);

        var order = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.New, order.Status);
        var eur = await BalanceOf(paper, "EUR");
        Assert.Equal(140.9469m, eur!.Available);
        Assert.Equal(59.0531m, eur.Reserved);
        Assert.Equal(200m, eur.Total);
    }

    [Fact]
    public async Task RestingBuy_FillsAtLimitWithMakerFee_OnlyWhenMarketTradesThrough()
    {
        var paper = Create();
        var placed = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: true, CancellationToken.None);

        paper.OnQuote(Quote(bid: 58_990.00m, bidSize: 1m, ask: 59_000.00m, askSize: 1m));
        Assert.Equal(OrderStatus.New, (await OrderOf(paper, placed)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        paper.OnQuote(Quote(bid: 58_890.00m, bidSize: 1m, ask: 58_900.00m, askSize: 0.005m));
        var order = await OrderOf(paper, placed);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(59m, order.FilledAmountQuote);
        Assert.Equal(0m, order.TotalFee);
        Assert.Equal(141.0000m, (await BalanceOf(paper, "EUR"))!.Available);
        Assert.Equal(0m, (await BalanceOf(paper, "EUR"))!.Reserved);
        Assert.Equal(0.001m, (await BalanceOf(paper, "BTC"))!.Available);
    }

    [Fact]
    public async Task CrossingBuy_FillsAtAskWithTakerFee_UpToShownSize_ThenRemainderFillsLater()
    {
        var paper = Create();

        var placed = await paper.PlaceLimitOrderAsync(Buy(0.003m, 60_050.00m), postOnly: false, CancellationToken.None);

        var partial = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.PartiallyFilled, partial.Status);
        Assert.Equal(0.002m, partial.FilledQuantity);
        Assert.Equal(120.02m, partial.FilledAmountQuote);
        Assert.Equal(0.108018m, partial.TotalFee);
        var eur = await BalanceOf(paper, "EUR");
        Assert.Equal(19.767937m, eur!.Available);
        Assert.Equal(60.104045m, eur.Reserved);

        // The book moves below the limit, but the remainder rests at its limit and never gets a better price.
        _time.Advance(TimeSpan.FromSeconds(1));
        paper.OnQuote(Quote(bid: 60_030.00m, bidSize: 1m, ask: 60_040.00m, askSize: 0.01m));

        var filled = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal(180.07m, filled.FilledAmountQuote);
        Assert.Equal(0.162063m, filled.TotalFee);
        eur = await BalanceOf(paper, "EUR");
        Assert.Equal(19.767937m, eur!.Available);
        Assert.Equal(0m, eur.Reserved);
        Assert.Equal(0.003m, (await BalanceOf(paper, "BTC"))!.Available);
    }

    [Fact]
    public async Task CrossingSell_FillsAtBidWithTakerFee()
    {
        var paper = Create(eur: 0m, btc: 0.01m);

        var placed = await paper.PlaceLimitOrderAsync(Sell(0.005m, 60_000.00m), postOnly: false, CancellationToken.None);

        var order = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.27m, order.TotalFee);
        Assert.Equal(299.73m, (await BalanceOf(paper, "EUR"))!.Available);
        var btc = await BalanceOf(paper, "BTC");
        Assert.Equal(0.005m, btc!.Available);
        Assert.Equal(0m, btc.Reserved);
    }

    [Fact]
    public async Task PostOnlyThatWouldCross_IsRejected_AndReservesNothing()
    {
        var paper = Create();

        var placed = await paper.PlaceLimitOrderAsync(Buy(0.001m, 60_010.00m), postOnly: true, CancellationToken.None);

        Assert.Equal(OrderStatus.Rejected, (await OrderOf(paper, placed)).Status);
        var eur = await BalanceOf(paper, "EUR");
        Assert.Equal(200m, eur!.Available);
        Assert.Equal(0m, eur.Reserved);
        Assert.Empty(await paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task InsufficientBalance_IsRejected_WithoutCreatingAnOrder()
    {
        var paper = Create();

        var ex = await Assert.ThrowsAsync<ExchangeException>(
            () => paper.PlaceLimitOrderAsync(Buy(0.004m, 60_010.00m), postOnly: false, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("paper-insufficient-balance", ex.ErrorId);
        Assert.Empty(await paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal(200m, (await BalanceOf(paper, "EUR"))!.Available);
    }

    [Theory]
    [InlineData("0.000000015", "59000.00", "paper-step")]
    [InlineData("0.001", "59000.005", "paper-step")]
    [InlineData("0.000001", "59000.00", "paper-size")]
    public async Task OrdersOffStepOrBelowMinimum_AreRejected(string size, string price, string errorId)
    {
        var paper = Create();
        var order = Buy(decimal.Parse(size, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        var ex = await Assert.ThrowsAsync<ExchangeException>(() => paper.PlaceLimitOrderAsync(order, postOnly: false, CancellationToken.None));

        Assert.Equal(errorId, ex.ErrorId);
    }

    [Fact]
    public async Task UnknownSymbol_IsRejected()
    {
        var paper = Create();

        var ex = await Assert.ThrowsAsync<ExchangeException>(() => paper.PlaceLimitOrderAsync(
            new OrderIntent("DOGE-EUR", OrderSide.Buy, 10m, 0.20m, "d-1"), postOnly: false, CancellationToken.None));

        Assert.Equal("paper-unknown-symbol", ex.ErrorId);
    }

    [Fact]
    public async Task StaleQuote_IsRejected()
    {
        var paper = Create();
        _time.Advance(TimeSpan.FromMinutes(3));

        var ex = await Assert.ThrowsAsync<ExchangeException>(
            () => paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: false, CancellationToken.None));

        Assert.Equal("paper-no-market-data", ex.ErrorId);
    }

    [Fact]
    public async Task SameClientOrderIdTwice_ReturnsSameOrder_AndReservesOnce()
    {
        var paper = Create();

        var first = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "dup"), postOnly: false, CancellationToken.None);
        var second = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "dup"), postOnly: false, CancellationToken.None);

        Assert.Equal(first.VenueOrderId, second.VenueOrderId);
        Assert.Single(await paper.GetOpenOrdersAsync(Btc, CancellationToken.None));
        Assert.Equal(59.0531m, (await BalanceOf(paper, "EUR"))!.Reserved);
    }

    [Fact]
    public async Task Cancel_OpenOrder_ReleasesFunds_UnknownReturnsFalse_RepeatIsNoOp()
    {
        var paper = Create();
        var placed = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: false, CancellationToken.None);

        Assert.True(await paper.CancelOrderAsync(placed.VenueOrderId, CancellationToken.None));
        Assert.True(await paper.CancelOrderAsync(placed.VenueOrderId, CancellationToken.None));
        Assert.False(await paper.CancelOrderAsync("no-such-order", CancellationToken.None));

        Assert.Equal(OrderStatus.Cancelled, (await OrderOf(paper, placed)).Status);
        var eur = await BalanceOf(paper, "EUR");
        Assert.Equal(200m, eur!.Available);
        Assert.Equal(0m, eur.Reserved);

        paper.OnQuote(Quote(bid: 58_000.00m, bidSize: 1m, ask: 58_010.00m, askSize: 1m));
        Assert.Equal(OrderStatus.Cancelled, (await OrderOf(paper, placed)).Status);
    }

    [Fact]
    public async Task OneQuote_SharesItsSizeAcrossRestingOrders_OldestFirst()
    {
        var paper = Create();
        var first = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "a"), postOnly: false, CancellationToken.None);
        var second = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "b"), postOnly: false, CancellationToken.None);

        paper.OnQuote(Quote(bid: 58_890.00m, bidSize: 1m, ask: 58_900.00m, askSize: 0.0015m));

        Assert.Equal(OrderStatus.Filled, (await OrderOf(paper, first)).Status);
        var partial = await OrderOf(paper, second);
        Assert.Equal(OrderStatus.PartiallyFilled, partial.Status);
        Assert.Equal(0.0005m, partial.FilledQuantity);
    }

    [Fact]
    public async Task OpenOrders_FilterBySymbol_AndUseDashSymbols()
    {
        var paper = Create();
        await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: false, CancellationToken.None);

        Assert.Single(await paper.GetOpenOrdersAsync("btc/eur", CancellationToken.None));
        Assert.Empty(await paper.GetOpenOrdersAsync("ETH-EUR", CancellationToken.None));
        Assert.Equal(Btc, (await paper.GetOpenOrdersAsync(null, CancellationToken.None))[0].Symbol);
    }

    [Fact]
    public async Task SizeTakenAtPlacement_IsNotAvailableToTheNextOrder()
    {
        var paper = Create();

        var first = await paper.PlaceLimitOrderAsync(Buy(0.002m, 60_050.00m, "a"), postOnly: false, CancellationToken.None);
        var second = await paper.PlaceLimitOrderAsync(Buy(0.001m, 60_050.00m, "b"), postOnly: false, CancellationToken.None);

        Assert.Equal(OrderStatus.Filled, (await OrderOf(paper, first)).Status);
        var waiting = await OrderOf(paper, second);
        Assert.Equal(OrderStatus.New, waiting.Status);
        Assert.Equal(0m, waiting.FilledQuantity);
    }

    [Fact]
    public async Task SameClientOrderId_WithDifferentDetails_IsRejected()
    {
        var paper = Create();
        await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "dup"), postOnly: false, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ExchangeException>(
            () => paper.PlaceLimitOrderAsync(Buy(0.002m, 59_000.00m, "dup"), postOnly: false, CancellationToken.None));

        Assert.Equal("paper-duplicate-client-order-id", ex.ErrorId);
    }

    [Fact]
    public async Task OlderQuote_ArrivingLate_IsIgnored()
    {
        var paper = Create();
        var placed = await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m), postOnly: false, CancellationToken.None);

        paper.OnQuote(new MarketQuote(Btc, 57_990.00m, 1m, 58_000.00m, 1m, T0.AddSeconds(-30)));

        Assert.Equal(OrderStatus.New, (await OrderOf(paper, placed)).Status);
    }

    [Fact]
    public async Task RestingSell_FillsAtLimitWithMakerFee_WhenBidTradesThrough()
    {
        var paper = Create(eur: 0m, btc: 0.01m);
        var placed = await paper.PlaceLimitOrderAsync(Sell(0.001m, 61_000.00m), postOnly: true, CancellationToken.None);
        Assert.Equal(0.001m, (await BalanceOf(paper, "BTC"))!.Reserved);

        _time.Advance(TimeSpan.FromSeconds(1));
        paper.OnQuote(Quote(bid: 61_000.01m, bidSize: 1m, ask: 61_010.00m, askSize: 1m));

        var order = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0m, order.TotalFee);
        Assert.Equal(61m, (await BalanceOf(paper, "EUR"))!.Available);
        var btc = await BalanceOf(paper, "BTC");
        Assert.Equal(0.009m, btc!.Available);
        Assert.Equal(0m, btc.Reserved);
    }

    [Fact]
    public async Task CancelAfterPartialFill_ReleasesOnlyTheRemainder()
    {
        var paper = Create();
        var placed = await paper.PlaceLimitOrderAsync(Buy(0.003m, 60_050.00m), postOnly: false, CancellationToken.None);

        await paper.CancelOrderAsync(placed.VenueOrderId, CancellationToken.None);

        var order = await OrderOf(paper, placed);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0.002m, order.FilledQuantity);
        var eur = await BalanceOf(paper, "EUR");
        Assert.Equal(79.871982m, eur!.Available);
        Assert.Equal(0m, eur.Reserved);
    }

    [Fact]
    public async Task FullyReservedBalance_WithFractionalAmounts_NeverGoesNegative()
    {
        // Spike-sized order (0.00000172 BTC at 58,397.08) with exactly enough EUR, filled in two parts.
        var paper = Create(eur: 0.10053337627984m);
        _time.Advance(TimeSpan.FromSeconds(1));
        paper.OnQuote(Quote(bid: 58_397.07m, bidSize: 1m, ask: 58_397.08m, askSize: 0.000001m));

        var placed = await paper.PlaceLimitOrderAsync(Buy(0.00000172m, 58_397.08m), postOnly: false, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        paper.OnQuote(Quote(bid: 58_397.07m, bidSize: 1m, ask: 58_397.08m, askSize: 1m));

        Assert.Equal(OrderStatus.Filled, (await OrderOf(paper, placed)).Status);
        var eur = await BalanceOf(paper, "EUR");
        Assert.True(eur is null || (eur.Available >= 0 && eur.Reserved == 0), $"EUR went negative: {eur}");
    }

    [Fact]
    public async Task PairRules_AreReloadedAfterTheRefreshInterval()
    {
        var market = new FakeMarketData();
        var options = new PaperOptions { PairsRefreshInterval = TimeSpan.FromHours(1) };
        options.InitialBalances["EUR"] = 200m;
        var paper = new PaperExchange(market, options, _time);
        paper.OnQuote(Quote(bid: 60_000.00m, bidSize: 1m, ask: 60_010.00m, askSize: 1m));

        await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "a"), postOnly: false, CancellationToken.None);
        await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "b"), postOnly: false, CancellationToken.None);
        Assert.Equal(1, market.PairCalls);

        _time.Advance(TimeSpan.FromHours(2));
        paper.OnQuote(Quote(bid: 60_000.00m, bidSize: 1m, ask: 60_010.00m, askSize: 1m));
        await paper.PlaceLimitOrderAsync(Buy(0.001m, 59_000.00m, "c"), postOnly: false, CancellationToken.None);

        Assert.Equal(2, market.PairCalls);
    }

    [Fact]
    public void Constructor_UnrealisticFee_Throws()
    {
        var options = new PaperOptions { TakerFeeRate = 0.05m };

        Assert.Throws<ArgumentException>(() => new PaperExchange(new FakeMarketData(), options, _time));
    }

    [Fact]
    public async Task MarketData_IsPassedThroughToTheRealExchange()
    {
        var paper = Create();

        var pairs = await paper.GetPairsAsync(CancellationToken.None);

        Assert.Contains(pairs, p => p.Symbol == Btc);
    }
}
