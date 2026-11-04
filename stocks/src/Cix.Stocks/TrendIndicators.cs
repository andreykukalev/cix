using Cix.Domain.Exchange;

namespace Cix.Stocks;

/// <summary>
/// Indicators the trend rules need beyond <see cref="Cix.Domain.MarketData.Indicators"/>. Each returns null when there
/// isn't enough data. Bars are daily and in ascending order.
/// </summary>
public static class TrendIndicators
{
    /// <summary>Simple moving average of the last <paramref name="period"/> closes.</summary>
    public static decimal? Sma(IReadOnlyList<Candle> bars, int period) => Sma(bars, period, bars.Count);

    /// <summary>Simple moving average of the <paramref name="period"/> closes before index <paramref name="end"/> (exclusive).</summary>
    public static decimal? Sma(IReadOnlyList<Candle> bars, int period, int end)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        if (end > bars.Count || end < period)
            return null;

        var sum = 0m;
        for (var i = end - period; i < end; i++)
            sum += bars[i].Close;
        return sum / period;
    }

    /// <summary>The highest close of the <paramref name="lookback"/> bars before the last one, so a new high can be compared with it.</summary>
    public static decimal? PriorHighestClose(IReadOnlyList<Candle> bars, int lookback)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lookback);
        if (bars.Count < lookback + 1)
            return null;

        var high = decimal.MinValue;
        for (var i = bars.Count - 1 - lookback; i < bars.Count - 1; i++)
            high = Math.Max(high, bars[i].Close);
        return high;
    }

    /// <summary>The highest close on or after <paramref name="since"/>, or null if no bar is that recent.</summary>
    public static decimal? HighestCloseSince(IReadOnlyList<Candle> bars, DateOnly since)
    {
        ArgumentNullException.ThrowIfNull(bars);
        decimal? high = null;
        for (var i = bars.Count - 1; i >= 0 && TradingDay(bars[i]) >= since; i--)
            high = high is { } h ? Math.Max(h, bars[i].Close) : bars[i].Close;
        return high;
    }

    /// <summary>The trading day a daily bar belongs to (its start date, in UTC).</summary>
    public static DateOnly TradingDay(Candle bar) => DateOnly.FromDateTime(bar.Start.UtcDateTime);
}
