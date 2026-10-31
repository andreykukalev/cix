using Cix.Domain.Exchange;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Tests;

public class PaperPortfolioTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 26);
    private static readonly DateOnly Day2 = Day1.AddDays(1);
    private static readonly DateOnly Day3 = Day1.AddDays(2);

    // Round numbers: 1% slippage, 0.1% commission (at least 1), 0.2% FX fee.
    private static readonly SimulationSettings Costs = new()
    {
        CommissionFraction = 0.001m, MinCommission = 1m, FxFeeFraction = 0.002m, SlippageFraction = 0.01m,
    };

    private static readonly Instrument Xyz = new("XYZ.US", "XYZ Corp", "USD", InstrumentKind.Stock);

    private static PaperPortfolio Portfolio(decimal cash = 10_000m, string baseCurrency = "USD", IReadOnlyDictionary<string, decimal>? fx = null) =>
        new(baseCurrency, cash, [Xyz], fx ?? new Dictionary<string, decimal>(), Costs);

    private static Candle Bar(DateOnly day, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), open, high, low, close, 1000m);

    private static Dictionary<string, Candle> Bars(Candle bar) => new() { [Xyz.Symbol] = bar };

    private static Recommendation Buy(decimal quantity = 10m, decimal? stop = 90m) =>
        new(ActionKind.Buy, Xyz.Symbol, quantity, 100m, stop, Reasons.Breakout);

    /// <summary>Bought on day 1: 10 at 100 × 1.01 = 101, value 1010, commission max(1, 1.01) = 1.01, cash 8988.99.</summary>
    private static PaperPortfolio Bought()
    {
        var portfolio = Portfolio();
        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 105m, 95m, 102m)), [Buy()]);
        return portfolio;
    }

    [Fact]
    public void ExecuteDay_ABuyFillsAtTheOpenWithSlippageAndFees()
    {
        var portfolio = Bought();

        Assert.Equal(8988.99m, portfolio.Cash);
        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(new Holding(Xyz.Symbol, 10m, 101m, 90m, Day1), holding);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(new PaperTrade(Day1, Xyz.Symbol, TradeKind.Buy, 10m, 101m, 1.01m, null, Reasons.Breakout), trade);
    }

    [Fact]
    public void ExecuteDay_AStopTouchedDuringTheDaySellsAtTheStop()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 95m, 96m, 89m, 92m)), []);

        // 90 × 0.99 = 89.1, value 891, commission 1: cash 8988.99 + 890, profit 890 − 1011.01.
        Assert.Empty(portfolio.Holdings);
        Assert.Equal(9878.99m, portfolio.Cash);
        var trade = portfolio.Trades[^1];
        Assert.Equal(new PaperTrade(Day2, Xyz.Symbol, TradeKind.StopOut, 10m, 89.1m, 1m, -121.01m, "stop"), trade);
    }

    [Fact]
    public void ExecuteDay_AGapUnderTheStopSellsAtTheOpen()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 85m, 86m, 80m, 82m)), []);

        // 85 × 0.99 = 84.15, value 841.5, commission 1: cash 8988.99 + 840.5, profit 840.5 − 1011.01.
        Assert.Equal(9829.49m, portfolio.Cash);
        var trade = portfolio.Trades[^1];
        Assert.Equal(TradeKind.StopOut, trade.Kind);
        Assert.Equal(84.15m, trade.Price);
        Assert.Equal(-170.51m, trade.ProfitBase);
    }

    [Fact]
    public void ExecuteDay_ABuyCanBeStoppedOutOnItsFirstDay()
    {
        var portfolio = Portfolio();

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 101m, 89m, 92m)), [Buy()]);

        // Bought at 101 (cash 8988.99), then the low reaches the stop: 90 × 0.99 = 89.1, value 891, commission 1.
        Assert.Empty(portfolio.Holdings);
        Assert.Equal([TradeKind.Buy, TradeKind.StopOut], portfolio.Trades.Select(t => t.Kind));
        Assert.Equal(9878.99m, portfolio.Cash);
    }

    [Fact]
    public void ExecuteDay_AStopRaisedAboveTheOpenSellsAtTheOpen()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 97m, 99m, 96m, 98m)), [new(ActionKind.RaiseStop, Xyz.Symbol, 10m, 99m, 98m, Reasons.Trail)]);

        // 97 × 0.99 = 96.03, value 960.3, commission 1: cash 8988.99 + 959.3.
        Assert.Empty(portfolio.Holdings);
        Assert.Equal(96.03m, portfolio.Trades[^1].Price);
        Assert.Equal(9948.29m, portfolio.Cash);
    }

    [Fact]
    public void ExecuteDay_ASellNeverCostsMoreThanItBrings()
    {
        var portfolio = Portfolio(cash: 10m);
        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 5m, 5.5m, 4.5m, 5m)), [Buy(stop: null)]);
        // (10 − 1) / (5.05 × 1.003) = 1.78: one share at 5.05 and commission 1.
        Assert.Equal(3.95m, portfolio.Cash);

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 0.5m, 0.6m, 0.4m, 0.5m)), [new(ActionKind.Sell, Xyz.Symbol, 1m, 0.5m, null, Reasons.TrendBroken)]);

        // 0.5 × 0.99 = 0.495, and the commission is capped at those proceeds.
        Assert.Equal(3.95m, portfolio.Cash);
        Assert.Equal(new PaperTrade(Day2, Xyz.Symbol, TradeKind.Sell, 1m, 0.495m, 0.495m, -6.05m, Reasons.TrendBroken), portfolio.Trades[^1]);
    }

    [Fact]
    public void ExecuteDay_ABuyIsSkippedWhenItOpensAtOrUnderItsStop()
    {
        var portfolio = Portfolio();

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 90m, 95m, 88m, 94m)), [Buy()]);

        Assert.Empty(portfolio.Trades);
        Assert.Equal(10_000m, portfolio.Cash);
        Assert.Equal(new SkippedAction(Day1, Xyz.Symbol, ActionKind.Buy, PaperPortfolio.Skips.OpenUnderStop), Assert.Single(portfolio.Skipped));
    }

    [Fact]
    public void ExecuteDay_ABuyIsCutToWhatTheCashPaysFor()
    {
        var portfolio = Portfolio(cash: 500m);

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 105m, 95m, 102m)), [Buy()]);

        // (500 − 1) / (101 × 1.003) = 4.93, floored to 4: value 404, commission 1.
        Assert.Equal(4m, Assert.Single(portfolio.Holdings).Quantity);
        Assert.Equal(95m, portfolio.Cash);
    }

    [Fact]
    public void ExecuteDay_ABuyWithoutEnoughCashForOneShareIsSkipped()
    {
        var portfolio = Portfolio(cash: 50m);

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 105m, 95m, 102m)), [Buy()]);

        Assert.Empty(portfolio.Holdings);
        Assert.Equal(PaperPortfolio.Skips.NoCash, Assert.Single(portfolio.Skipped).Reason);
    }

    [Fact]
    public void ExecuteDay_AForeignBuyPaysInTheBaseCurrencyWithTheFxFee()
    {
        var portfolio = Portfolio(baseCurrency: "EUR", fx: new Dictionary<string, decimal> { ["USD"] = 0.9m });

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 105m, 95m, 102m)), [Buy()]);

        // 10 × 101 × 0.9 = 909; commission max(1, 0.909) = 1, FX fee 909 × 0.002 = 1.818.
        Assert.Equal(9088.182m, portfolio.Cash);
        Assert.Equal(2.818m, Assert.Single(portfolio.Trades).FeesBase);
        // Equity at a close of 110: 9088.182 + 10 × 110 × 0.9.
        Assert.Equal(10_078.182m, portfolio.Equity(new Dictionary<string, decimal> { [Xyz.Symbol] = 110m }));
    }

    [Fact]
    public void ExecuteDay_AForeignBuyWithoutARateIsSkipped()
    {
        var portfolio = Portfolio(baseCurrency: "EUR");

        portfolio.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 105m, 95m, 102m)), [Buy()]);

        Assert.Empty(portfolio.Holdings);
        Assert.Equal(PaperPortfolio.Skips.NoFxRate, Assert.Single(portfolio.Skipped).Reason);
    }

    [Fact]
    public void ExecuteDay_StopsAreOnlyRaised()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 100m, 101m, 99m, 100m)), [new(ActionKind.RaiseStop, Xyz.Symbol, 10m, 100m, 95m, Reasons.Trail)]);
        Assert.Equal(95m, Assert.Single(portfolio.Holdings).Stop);

        portfolio.ExecuteDay(Day3, Bars(Bar(Day3, 100m, 101m, 99m, 100m)), [new(ActionKind.SetStop, Xyz.Symbol, 10m, 100m, 85m, Reasons.NoStop)]);
        Assert.Equal(95m, Assert.Single(portfolio.Holdings).Stop);
    }

    [Fact]
    public void ExecuteDay_ASellFillsAtTheOpen()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 110m, 111m, 108m, 109m)), [new(ActionKind.Sell, Xyz.Symbol, 10m, 109m, null, Reasons.TrendBroken)]);

        // 110 × 0.99 = 108.9, value 1089, commission 1.089: cash 8988.99 + 1087.911, profit 1087.911 − 1011.01.
        Assert.Empty(portfolio.Holdings);
        Assert.Equal(10_076.901m, portfolio.Cash);
        Assert.Equal(new PaperTrade(Day2, Xyz.Symbol, TradeKind.Sell, 10m, 108.9m, 1.089m, 76.901m, Reasons.TrendBroken), portfolio.Trades[^1]);
    }

    [Fact]
    public void ExecuteDay_ActionsThatDontFitTheHoldingsAreSkipped()
    {
        var portfolio = Bought();

        portfolio.ExecuteDay(Day2, Bars(Bar(Day2, 100m, 101m, 99m, 100m)), [Buy()]);
        Assert.Equal(PaperPortfolio.Skips.AlreadyHeld, Assert.Single(portfolio.Skipped).Reason);

        var empty = Portfolio();
        empty.ExecuteDay(Day1, Bars(Bar(Day1, 100m, 101m, 99m, 100m)), [new(ActionKind.Sell, Xyz.Symbol, 10m, 100m, null, Reasons.TrendBroken)]);
        Assert.Equal(PaperPortfolio.Skips.NotHeld, Assert.Single(empty.Skipped).Reason);
    }

    [Fact]
    public void ExecuteDay_AdviceForASymbolWithoutABarWaits()
    {
        var portfolio = Portfolio();
        var buy = Buy();

        var waiting = portfolio.ExecuteDay(Day1, new Dictionary<string, Candle>(), [buy]);

        Assert.Equal(buy, Assert.Single(waiting));
        Assert.Empty(portfolio.Trades);
        Assert.Empty(portfolio.Skipped);
    }

    [Fact]
    public void Equity_AHoldingWithoutACloseIsValuedAtItsCost()
    {
        var portfolio = Bought();

        Assert.Equal(8988.99m + 1010m, portfolio.Equity(new Dictionary<string, decimal>()));
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("0.051")]
    public void Settings_FractionsOutOfRangeAreRefused(string fraction)
    {
        var settings = new SimulationSettings { SlippageFraction = decimal.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void ASavedPortfolioPicksUpWithItsPositionsAndCost()
    {
        var saved = Bought();

        var restored = new PaperPortfolio("USD", saved.Cash, saved.Positions, [Xyz], new Dictionary<string, decimal>(), Costs);
        restored.ExecuteDay(Day2, Bars(Bar(Day2, 110m, 111m, 109m, 110m)), [new(ActionKind.Sell, Xyz.Symbol, 10m, 110m, 90m, Reasons.TrendBroken)]);

        // Sold 10 at 110 × 0.99 = 108.9: 1089 less commission 1.089, against the cost of 1010 + 1.01.
        var sell = Assert.Single(restored.Trades);
        Assert.Equal(1089m - 1.089m - 1011.01m, sell.ProfitBase);
        Assert.Empty(restored.Positions);
    }

    [Fact]
    public void ASavedPortfolioWithAnEmptyOrRepeatedPositionIsRefused()
    {
        var position = Assert.Single(Bought().Positions);

        Assert.Throws<ArgumentException>(() => new PaperPortfolio("USD", 0m, [position, position], [Xyz], new Dictionary<string, decimal>(), Costs));
        Assert.Throws<ArgumentException>(() => new PaperPortfolio(
            "USD", 0m, [position with { Holding = position.Holding with { Quantity = 0m } }], [Xyz], new Dictionary<string, decimal>(), Costs));
    }
}
