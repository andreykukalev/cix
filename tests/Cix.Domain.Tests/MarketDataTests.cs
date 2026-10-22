using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Domain.Tests;

public sealed class IndicatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    private static Candle C(int i, decimal high, decimal low, decimal close, decimal volume = 1m) =>
        new(T0.AddHours(i), close, high, low, close, volume);

    [Fact]
    public void Ema_Period3_OverOneToTen_IsNine()
    {
        // SMA seed of 1,2,3 = 2; k = 0.5; each step moves halfway to the next value: 3, 4, ... 9.
        var values = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();

        Assert.Equal(9m, Indicators.Ema(values, 3));
    }

    [Fact]
    public void Ema_Period5_OverOneToTen_IsEight()
    {
        var values = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();

        Assert.Equal(8m, Math.Round(Indicators.Ema(values, 5)!.Value, 10));
    }

    [Fact]
    public void Ema_TooFewValues_IsNull() => Assert.Null(Indicators.Ema([1m, 2m], 3));

    [Fact]
    public void Atr_ConstantRangeWithoutGaps_EqualsTheRange()
    {
        var candles = Enumerable.Range(0, 20).Select(i => C(i, high: 101m, low: 99m, close: 100m)).ToList();

        Assert.Equal(2m, Indicators.Atr(candles, 14));
    }

    [Fact]
    public void Atr_UsesPreviousCloseForGaps_AndWilderSmoothing()
    {
        // True ranges: 2 (gap up from 10 to high 12), 1, then 3.5 (gap from 10.5 to high 14).
        // Seed ATR(2) = (2 + 1) / 2 = 1.5; next = (1.5 * 1 + 3.5) / 2 = 2.5.
        var candles = new List<Candle>
        {
            C(0, 10m, 9m, 10m),
            C(1, 12m, 11m, 11m),
            C(2, 11m, 10m, 10.5m),
            C(3, 14m, 13m, 13m),
        };

        Assert.Equal(2.5m, Indicators.Atr(candles, 2));
    }

    [Fact]
    public void Atr_NeedsPeriodPlusOneCandles() =>
        Assert.Null(Indicators.Atr([C(0, 1m, 1m, 1m), C(1, 1m, 1m, 1m)], 2));
}

public sealed class FeatureCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // 60 hourly candles closing 100, 101, ... 159; volume 1 for the first 36, 2 for the last 24.
    private static List<Candle> RisingMarket() =>
        Enumerable.Range(0, 60)
            .Select(i => new Candle(Now.AddHours(i - 60), 100m + i, 101m + i, 99m + i, 100m + i, i >= 36 ? 2m : 1m))
            .ToList();

    private static MarketQuote QuoteAt(DateTimeOffset asOf) => new("BTC-EUR", 159.9m, 1m, 160.1m, 1m, asOf);

    [Fact]
    public void Compute_RisingMarket_SummarisesTrendRangeAndVolume()
    {
        var features = FeatureCalculator.Compute("BTC-EUR", RisingMarket(), QuoteAt(Now), Now, TimeSpan.FromMinutes(2));

        Assert.False(features.IsStale);
        Assert.Equal(160m, features.Price);
        Assert.Equal(Trend.Up, features.Trend);
        Assert.Equal(0.185185m, features.Return24h); // from the close 24 hours earlier (135) to 160
        Assert.Equal(160m, features.High24h);
        Assert.Equal(135m, features.Low24h);
        Assert.Equal(1m, features.VolumeChange24h); // 48 vs 24
        Assert.Equal(0.00125m, features.SpreadFraction);
        Assert.Equal(60, features.HourlyCandles);
        Assert.NotNull(features.Ema50);
        Assert.NotNull(features.Atr14);
    }

    [Fact]
    public void Compute_OldQuote_IsStale()
    {
        var features = FeatureCalculator.Compute("BTC-EUR", RisingMarket(), QuoteAt(Now.AddMinutes(-3)), Now, TimeSpan.FromMinutes(2));

        Assert.True(features.IsStale);
    }

    [Fact]
    public void Compute_NoData_ReturnsUnknownsInsteadOfThrowing()
    {
        var features = FeatureCalculator.Compute("BTC-EUR", [], null, Now, TimeSpan.FromMinutes(2));

        Assert.True(features.IsStale);
        Assert.Null(features.Price);
        Assert.Equal(Trend.Unknown, features.Trend);
        Assert.Null(features.VolumeChange24h);
    }
}
