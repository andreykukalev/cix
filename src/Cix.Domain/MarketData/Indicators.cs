using Cix.Domain.Exchange;

namespace Cix.Domain.MarketData;

/// <summary>Standard technical indicators on decimal prices. Each returns null when there isn't enough data.</summary>
public static class Indicators
{
    /// <summary>
    /// Exponential moving average of the last value: seeded with the simple average of the first
    /// <paramref name="period"/> values, then smoothed with k = 2 / (period + 1).
    /// </summary>
    public static decimal? Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        if (values.Count < period)
            return null;

        var ema = 0m;
        for (var i = 0; i < period; i++)
            ema += values[i];
        ema /= period;

        var k = 2m / (period + 1);
        for (var i = period; i < values.Count; i++)
            ema += (values[i] - ema) * k;

        return ema;
    }

    /// <summary>
    /// Average true range with Wilder's smoothing. True range uses the previous close, so it needs
    /// <paramref name="period"/> + 1 candles.
    /// </summary>
    public static decimal? Atr(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        if (candles.Count < period + 1)
            return null;

        decimal TrueRange(int i)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            return Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
        }

        var atr = 0m;
        for (var i = 1; i <= period; i++)
            atr += TrueRange(i);
        atr /= period;

        for (var i = period + 1; i < candles.Count; i++)
            atr = ((atr * (period - 1)) + TrueRange(i)) / period;

        return atr;
    }

    /// <summary>Relative change from <paramref name="from"/> to <paramref name="to"/>, for example 0.05 for +5%.</summary>
    public static decimal? Change(decimal from, decimal to) => from == 0 ? null : (to - from) / from;
}
