using Cix.Domain.Exchange;

namespace Cix.Domain.MarketData;

public enum Trend
{
    Unknown,
    Down,
    Flat,
    Up,
}

/// <summary>
/// Compact market summary served to agents, so they never parse raw candles. Ratios are fractions
/// (0.05 means 5%). Null means not enough data yet.
/// </summary>
public sealed record FeatureSet(
    string Symbol,
    DateTimeOffset AsOf,
    bool IsStale,
    decimal? Price,
    decimal? Bid,
    decimal? Ask,
    decimal? SpreadFraction,
    Trend Trend,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Atr14,
    decimal? AtrFraction,
    decimal? Return1h,
    decimal? Return24h,
    decimal? High24h,
    decimal? Low24h,
    decimal? FromHigh24hFraction,
    decimal? FromLow24hFraction,
    decimal? VolumeChange24h,
    int HourlyCandles);

/// <summary>Builds a <see cref="FeatureSet"/> from hourly candles (oldest first) and the latest quote.</summary>
public static class FeatureCalculator
{
    /// <summary>EMAs closer than this fraction count as a flat trend.</summary>
    public const decimal FlatBand = 0.001m;

    public static FeatureSet Compute(
        string symbol,
        IReadOnlyList<Candle> hourly,
        MarketQuote? quote,
        DateTimeOffset now,
        TimeSpan maxQuoteAge)
    {
        ArgumentNullException.ThrowIfNull(hourly);
        var closes = hourly.Select(c => c.Close).ToList();
        var lastClose = closes.Count > 0 ? closes[^1] : (decimal?)null;

        var price = quote?.Mid ?? lastClose;
        var ema20 = Indicators.Ema(closes, 20);
        var ema50 = Indicators.Ema(closes, 50);
        var atr = Indicators.Atr(hourly, 14);

        var last24 = hourly.Skip(Math.Max(0, hourly.Count - 24)).ToList();
        var prior24 = hourly.Skip(Math.Max(0, hourly.Count - 48)).Take(Math.Max(0, Math.Min(24, hourly.Count - 24))).ToList();
        decimal? high = last24.Count > 0 ? last24.Max(c => c.High) : null;
        decimal? low = last24.Count > 0 ? last24.Min(c => c.Low) : null;

        decimal? volumeChange = null;
        if (last24.Count == 24 && prior24.Count == 24)
            volumeChange = Indicators.Change(prior24.Sum(c => c.Volume), last24.Sum(c => c.Volume));

        return new FeatureSet(
            symbol,
            now,
            IsStale: quote is null || now - quote.AsOf > maxQuoteAge,
            Price: Round(price, 8),
            Bid: quote?.Bid,
            Ask: quote?.Ask,
            SpreadFraction: quote is null || quote.Mid == 0 ? null : Round((quote.Ask - quote.Bid) / quote.Mid, 6),
            Trend: TrendOf(ema20, ema50),
            Ema20: Round(ema20, 8),
            Ema50: Round(ema50, 8),
            Atr14: Round(atr, 8),
            AtrFraction: atr is null || price is null or 0 ? null : Round(atr / price, 6),
            Return1h: closes.Count >= 2 && price is not null ? Round(Indicators.Change(closes[^2], price.Value), 6) : null,
            Return24h: closes.Count >= 25 && price is not null ? Round(Indicators.Change(closes[^25], price.Value), 6) : null,
            High24h: high,
            Low24h: low,
            FromHigh24hFraction: high is null || price is null ? null : Round(Indicators.Change(high.Value, price.Value), 6),
            FromLow24hFraction: low is null || price is null ? null : Round(Indicators.Change(low.Value, price.Value), 6),
            VolumeChange24h: Round(volumeChange, 6),
            HourlyCandles: hourly.Count);
    }

    private static Trend TrendOf(decimal? fast, decimal? slow)
    {
        if (fast is null || slow is null || slow == 0)
            return Trend.Unknown;

        var gap = (fast.Value - slow.Value) / slow.Value;
        return gap > FlatBand ? Trend.Up : gap < -FlatBand ? Trend.Down : Trend.Flat;
    }

    private static decimal? Round(decimal? value, int decimals) =>
        value is null ? null : Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero);
}
