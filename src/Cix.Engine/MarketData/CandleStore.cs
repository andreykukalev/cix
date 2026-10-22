using Cix.Domain.Exchange;

namespace Cix.Engine.MarketData;

/// <summary>Closed candles per symbol and interval. A Cosmos DB implementation follows with #6.</summary>
public interface ICandleStore
{
    /// <summary>Adds candles, ignoring any whose start time is already stored. Returns how many were new.</summary>
    int Upsert(string symbol, int intervalMinutes, IEnumerable<Candle> candles);

    Candle? Latest(string symbol, int intervalMinutes);

    /// <summary>The most recent <paramref name="count"/> candles, oldest first.</summary>
    IReadOnlyList<Candle> Recent(string symbol, int intervalMinutes, int count);
}

public sealed class InMemoryCandleStore(int maxPerSeries = 5_000) : ICandleStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Symbol, int Interval), SortedList<DateTimeOffset, Candle>> _series = [];

    public int Upsert(string symbol, int intervalMinutes, IEnumerable<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        lock (_gate)
        {
            var series = Series(symbol, intervalMinutes);
            var added = 0;
            foreach (var candle in candles)
            {
                if (series.TryAdd(candle.Start, candle))
                    added++;
            }

            while (series.Count > maxPerSeries)
                series.RemoveAt(0);

            return added;
        }
    }

    public Candle? Latest(string symbol, int intervalMinutes)
    {
        lock (_gate)
        {
            var series = Series(symbol, intervalMinutes);
            return series.Count == 0 ? null : series.Values[series.Count - 1];
        }
    }

    public IReadOnlyList<Candle> Recent(string symbol, int intervalMinutes, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            var values = Series(symbol, intervalMinutes).Values;
            return values.Skip(Math.Max(0, values.Count - count)).ToList();
        }
    }

    private SortedList<DateTimeOffset, Candle> Series(string symbol, int intervalMinutes)
    {
        var key = (Symbol: symbol.ToUpperInvariant(), Interval: intervalMinutes);
        if (!_series.TryGetValue(key, out var series))
        {
            series = [];
            _series[key] = series;
        }

        return series;
    }
}
