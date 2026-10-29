namespace Cix.Stocks.Tests;

public class TrendIndicatorsTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    [Fact]
    public void SmaAveragesTheLastCloses()
    {
        var bars = AdvisorTests.Line(1m, 1m, Today, count: 5); // closes 1..5

        Assert.Equal(4m, TrendIndicators.Sma(bars, 3));
        Assert.Equal(3m, TrendIndicators.Sma(bars, 5));
        Assert.Equal(2m, TrendIndicators.Sma(bars, 3, end: 3));
        Assert.Null(TrendIndicators.Sma(bars, 6));
    }

    [Fact]
    public void PriorHighestCloseLeavesOutTheLastBar()
    {
        var bars = AdvisorTests.Line(1m, 1m, Today, count: 5);

        Assert.Equal(4m, TrendIndicators.PriorHighestClose(bars, 2));
        Assert.Equal(4m, TrendIndicators.PriorHighestClose(bars, 4));
        Assert.Null(TrendIndicators.PriorHighestClose(bars, 5));
    }

    [Fact]
    public void HighestCloseSinceADayIncludesThatDay()
    {
        var falling = AdvisorTests.Line(10m, -1m, Today, count: 5); // 10, 9, 8, 7, 6 ending today

        Assert.Equal(8m, TrendIndicators.HighestCloseSince(falling, Today.AddDays(-2)));
        Assert.Equal(10m, TrendIndicators.HighestCloseSince(falling, Today.AddDays(-30)));
        Assert.Null(TrendIndicators.HighestCloseSince(falling, Today.AddDays(1)));
    }

    [Theory]
    [InlineData("AAPL.US", true)]
    [InlineData("BRK-B.US", true)]
    [InlineData("CSPX.LSE", true)]
    [InlineData("SAP.XETRA", true)]
    [InlineData("AAPL", false)]
    [InlineData("aapl.us", false)]
    [InlineData("../eod.US", false)]
    [InlineData("A/B.US", false)]
    [InlineData("AAPL.US?x=1", false)]
    [InlineData("AAPL.US&api_token=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SymbolsAreTickerDotExchange(string? symbol, bool valid) =>
        Assert.Equal(valid, StockSymbol.IsValid(symbol));
}
