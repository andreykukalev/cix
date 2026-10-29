using Cix.Domain.Exchange;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Tests;

public class BacktesterTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);
    private static readonly DateOnly From = Today.AddDays(-20);
    private static readonly Instrument Xyz = new("XYZ.US", "XYZ Corp", "USD", InstrumentKind.Stock);
    private static readonly AdvisorSettings Advisor = new AdvisorSettings { BenchmarkSymbol = "VTI.US" }.Validate();
    private static readonly SimulationSettings Costs = new()
    {
        CommissionFraction = 0.001m, MinCommission = 1m, FxFeeFraction = 0m, SlippageFraction = 0.01m,
    };

    // 300 daily bars ending today, close_i = 100 + 0.1 i with the open at the close and ATR 1: every close is a new
    // 50-day high in a rising market, so the advisor buys after the first close of the run. From is bar 279 (close 127.9).
    private static List<Candle> Rising() => AdvisorTests.Line(100m, 0.1m, Today, 300);

    private static BacktestResult Run(List<Candle> bars, List<Candle>? benchmark = null) =>
        Backtester.Run([new PriceHistory(Xyz, bars)], benchmark ?? Rising(), From, Today, "USD", 10_000m, new Dictionary<string, decimal>(), Advisor, Costs);

    [Fact]
    public void Run_AdviceAfterACloseIsCarriedOutAtTheNextOpen()
    {
        var result = Run(Rising());

        // Sized on close 127.9: risk 1% of 10000 at 3 + 1 ATR is 25 shares, the 20% cap 2000 / 127.9 is 15.
        // Bought on From + 1 at its open 128.0 × 1.01.
        var buy = result.Trades[0];
        Assert.Equal(new PaperTrade(From.AddDays(1), Xyz.Symbol, TradeKind.Buy, 15m, 129.28m, 1.9392m, null, Reasons.Breakout), buy);
        Assert.Equal(21, result.Equity.Count);
        Assert.Equal(From, result.Equity[0].Day);
        Assert.Equal(10_000m, result.Equity[0].Equity);
        Assert.Equal(0, result.Equity[0].Positions);
    }

    [Fact]
    public void Run_AGapUnderTheStopSellsAtTheOpen()
    {
        var bars = Rising();
        // Bar 284 (From + 5) opens at 50, far under the stop near 125.
        bars[284] = bars[284] with { Open = 50m, High = 51m, Low = 49m, Close = 50m };

        var result = Run(bars);

        var stopOut = Assert.Single(result.Trades, t => t.Kind == TradeKind.StopOut);
        Assert.Equal(From.AddDays(5), stopOut.Day);
        Assert.Equal(49.5m, stopOut.Price);
    }

    [Fact]
    public void Run_ABuyWaitsForItsExchangeAndRunsOnTheRepeatedAdvice()
    {
        var bars = Rising();
        // XYZ's exchange is closed on From + 1 (bar 280); the benchmark's isn't.
        bars.RemoveAt(280);

        var result = Run(bars);

        // The From + 1 close repeats the buy from XYZ's still fresh From bar; it runs at the From + 2 open, 128.1 × 1.01.
        var buy = Assert.Single(result.Trades, t => t.Kind == TradeKind.Buy);
        Assert.Equal(From.AddDays(2), buy.Day);
        Assert.Equal(129.381m, buy.Price);
    }

    [Fact]
    public void Run_AWaitingBuyTheNewerAdviceDropsIsNotCarriedOut()
    {
        var bars = Rising();
        bars.RemoveAt(280);
        var benchmark = Rising();
        // The benchmark closes under its 200-day average on From + 1: risk-off, so that close advises no entry. It is back
        // above the next day, so XYZ is bought only at the From + 3 open.
        benchmark[280] = benchmark[280] with { Open = 50m, High = 51m, Low = 49m, Close = 50m };

        var result = Run(bars, benchmark);

        Assert.Equal(From.AddDays(3), result.Trades[0].Day);
    }

    [Fact]
    public void Run_ARangeWithoutBarsIsRefused() =>
        Assert.Throws<ArgumentException>(() => Backtester.Run(
            [new PriceHistory(Xyz, Rising())], Rising(), Today.AddDays(10), Today.AddDays(20), "USD", 10_000m,
            new Dictionary<string, decimal>(), Advisor, Costs));

    [Fact]
    public void Run_TheBenchmarkIsHeldFromTheFirstClose()
    {
        var result = Run(Rising());

        // 10000 × 129.9 / 127.9 at the last bar (299).
        Assert.Equal(10_000m * 129.9m / 127.9m, result.Equity[^1].Benchmark);
        Assert.Equal(0, result.Benchmark.RoundTrips);
        Assert.Equal(1m, result.Benchmark.TimeInMarket);
    }

    [Fact]
    public void Run_ARangeThatEndsBeforeItStartsIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            Backtester.Run([], Rising(), Today, From, "USD", 10_000m, new Dictionary<string, decimal>(), Advisor, Costs));

    [Fact]
    public void Stats_ReturnDrawdownAndWinRate()
    {
        var curve = new List<(DateOnly, decimal)>
        {
            (new DateOnly(2020, 1, 1), 100m), (new DateOnly(2020, 7, 1), 150m), (new DateOnly(2021, 1, 1), 120m),
        };
        PaperTrade Closed(decimal? profit) => new(new DateOnly(2020, 7, 1), "XYZ.US", TradeKind.Sell, 1m, 1m, 0m, profit, "x");

        var stats = Backtester.Stats(curve, [Closed(10m), Closed(-5m), Closed(null)], daysInMarket: 2);

        // 1.2 over 366 days: 1.2^(365.25 / 366) − 1 = 0.19955.
        Assert.Equal(new BacktestStats(0.2m, 0.1996m, 0.2m, 2, 0.5m, 0.6667m), stats);
    }

    [Fact]
    public void Stats_AShortSpanHasNoCagrAndALossOfEverythingIsMinusOne()
    {
        var day = new DateOnly(2020, 1, 1);

        Assert.Null(Backtester.Stats([(day, 100m), (day.AddDays(10), 130m)], null, 2).Cagr);
        Assert.Equal(-1m, Backtester.Stats([(day, 100m), (day.AddDays(400), 0m)], null, 2).Cagr);
    }

    [Fact]
    public void Stats_AnEmptyCurveIsAllZeros() =>
        Assert.Equal(new BacktestStats(0m, null, 0m, 0, null, 0m), Backtester.Stats([], null, 0));
}
