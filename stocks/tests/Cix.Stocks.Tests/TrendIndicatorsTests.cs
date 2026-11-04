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
    public void MovesAreTheChangesToTheLastCloseOverASessionAWeekAndAMonth()
    {
        var bars = AdvisorTests.Line(1m, 1m, Today, count: 25); // closes 1..25

        // 25 / 24, 25 / 20 and 25 / 4, less one, to four places; too short a history for the half year.
        Assert.Equal(new PriceMoves(0.0417m, 0.25m, 5.25m, null), TrendIndicators.Moves(bars));
        // 127 / 1 less one over the 126 sessions of half a year.
        Assert.Equal(126m, TrendIndicators.Moves(AdvisorTests.Line(1m, 1m, Today, count: 127)).HalfYear);
        // Too short a history for the week and the month: only what it covers.
        Assert.Equal(new PriceMoves(1m, null, null, null), TrendIndicators.Moves(AdvisorTests.Line(1m, 1m, Today, count: 2)));
        Assert.Equal(new PriceMoves(null, null, null, null), TrendIndicators.Moves(AdvisorTests.Line(1m, 1m, Today, count: 1)));
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
    [InlineData("AAPL.US\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SymbolsAreTickerDotExchange(string? symbol, bool valid) =>
        Assert.Equal(valid, StockSymbol.IsValid(symbol));
}
