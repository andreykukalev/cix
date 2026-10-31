using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Stocks;

/// <summary>A listed stock from the exchange's symbol list: the market-wide search's universe.</summary>
/// <param name="Symbol">TICKER.EXCHANGE, for example <c>AAPL.US</c>.</param>
/// <param name="Venue">Where it trades, for example <c>NASDAQ</c> or <c>NYSE</c>; over-the-counter venues are left out.</param>
/// <param name="Type">The provider's instrument type, for example <c>Common Stock</c>.</param>
public sealed record ListedStock(string Symbol, string Name, string Venue, string Type);

/// <summary>
/// One stock's last session from the provider's whole-market download, with the provider's own figures for it. The averages
/// are exponential (EMA), so they only shortlist: the advisor's rule is checked on the stock's own bars afterwards.
/// </summary>
public sealed record MarketRow(
    string Symbol, DateOnly Day, decimal Close, decimal Volume, decimal? MarketCap, decimal? Ema50, decimal? Ema200, decimal? High250,
    decimal? AverageVolume50);

/// <summary>A stock the market-wide search found, judged by the advisor's own entry rule and sized by its rules.</summary>
/// <param name="State"><see cref="EntryState.Breakout"/> (an entry signal) or <see cref="EntryState.Uptrend"/> near its high.</param>
/// <param name="Shares">What the advisor's sizing gives on the capital (risk to the stop, the position cap), before cash.</param>
/// <param name="Momentum">The change over <see cref="AdvisorSettings.MomentumDays"/> sessions, the advisor's ranking.</param>
public sealed record Candidate(
    string Symbol, string? Name, string? Venue, DateOnly Day, EntryState State, decimal Close, decimal PriorHigh, decimal SlowAverage,
    decimal Atr, decimal Stop, decimal Shares, decimal Momentum, decimal? MarketCap);

/// <summary>The market-wide search's filters (ADR-0025). Money in the market's currency (USD for <c>US</c>).</summary>
public sealed record ScreenerSettings
{
    /// <summary>Off by default: the search needs EODHD's whole-market download, which the free key doesn't have.</summary>
    public bool Enabled { get; init; }

    /// <summary>The provider's exchange code for the whole market, for example <c>US</c>.</summary>
    public string Exchange { get; init; } = "US";

    /// <summary>The venues a stock must trade on: the main exchanges, no over-the-counter.</summary>
    public IReadOnlyList<string> Venues { get; init; } = ["NYSE", "NASDAQ", "NYSE MKT", "AMEX"];

    /// <summary>The instrument type kept from the symbol list.</summary>
    public string Type { get; init; } = "Common Stock";

    /// <summary>No penny stocks: their spreads and gaps break the stop.</summary>
    public decimal MinPrice { get; init; } = 5m;

    public decimal MinMarketCap { get; init; } = 2_000_000_000m;

    /// <summary>The 50-day average volume times the close: enough trading that an order and a stop fill near their price.</summary>
    public decimal MinDollarVolume { get; init; } = 20_000_000m;

    /// <summary>How far under its 250-day high a stock may close and still be shortlisted (0.15: within 15%).</summary>
    public decimal MaxBelowHigh { get; init; } = 0.15m;

    /// <summary>The stocks whose own bars are fetched (one call each) to check the advisor's rule.</summary>
    public int ShortlistSize { get; init; } = 40;

    /// <summary>An uptrend this close under its prior closing high is listed too (0.03: within 3%), as one to watch.</summary>
    public decimal NearHighFraction { get; init; } = 0.03m;

    /// <summary>The most candidates listed.</summary>
    public int MaxCandidates { get; init; } = 15;

    /// <summary>EODHD calls the search may spend in a UTC day: the download is 100, the symbol list 1, each stock's bars 1.</summary>
    public int MaxCallsPerDay { get; init; } = 300;

    /// <summary>How often the symbol list is fetched again, in days.</summary>
    public int UniverseDays { get; init; } = 7;

    public ScreenerSettings Validate()
    {
        if (string.IsNullOrWhiteSpace(Exchange) || Exchange.Length > 10 || !Exchange.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"Screener exchange '{Exchange}' isn't an exchange code.");
        if (Venues is not { Count: > 0 } || Venues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("The screener needs at least one venue.");
        if (string.IsNullOrWhiteSpace(Type))
            throw new ArgumentException("The screener needs an instrument type.");
        if (MinPrice < 0 || MinMarketCap < 0 || MinDollarVolume < 0)
            throw new ArgumentOutOfRangeException(nameof(MinPrice), "The screener's minimums can't be negative.");
        if (MaxBelowHigh is <= 0 or >= 1 || NearHighFraction is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBelowHigh), "Fractions must be in 0 to 1.");
        if (ShortlistSize is < 1 or > 500 || MaxCandidates is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(ShortlistSize), "The shortlist is 1 to 500 stocks, the candidates 1 to 100.");
        if (UniverseDays is < 1 or > 90)
            throw new ArgumentOutOfRangeException(nameof(UniverseDays), UniverseDays, "Must be 1 to 90 days.");
        // The download (100), the symbol list (1) and the shortlist's bars must fit, or the search could never run.
        if (MaxCallsPerDay < 101 + ShortlistSize || MaxCallsPerDay > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaxCallsPerDay), MaxCallsPerDay, $"Must be at least {101 + ShortlistSize} (the download, the symbol list and the shortlist) and at most 100 000.");
        return this;
    }
}

/// <summary>
/// The market-wide search (ADR-0025), with no I/O: first a shortlist from the whole market's last session and the provider's
/// own averages, then the advisor's entry rule (<see cref="Advisor.Entry"/>) on each shortlisted stock's own bars.
/// </summary>
public static class Screener
{
    /// <summary>
    /// The stocks worth fetching bars for: listed common stocks on the main venues, priced from <see cref="ScreenerSettings.MinPrice"/>
    /// to <paramref name="maxSharePrice"/>, large and traded enough, in an uptrend by the provider's averages and near their
    /// 250-day high; nearest the high first, then the most traded. <paramref name="exclude"/> (the watchlist, the holdings)
    /// is left out: those are advised already.
    /// </summary>
    public static IReadOnlyList<MarketRow> Shortlist(
        IEnumerable<MarketRow> rows, IReadOnlyDictionary<string, ListedStock> universe, DateOnly day, decimal? maxSharePrice,
        IReadOnlySet<string> exclude, ScreenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentNullException.ThrowIfNull(settings);
        var venues = settings.Venues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => r.Day == day && !exclude.Contains(r.Symbol)
                        && universe.TryGetValue(r.Symbol, out var listed)
                        && venues.Contains(listed.Venue) && string.Equals(listed.Type, settings.Type, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Close >= settings.MinPrice && r.Close > 0 && (maxSharePrice is not { } cap || r.Close <= cap))
            .Where(r => r.MarketCap >= settings.MinMarketCap)
            .Where(r => r.AverageVolume50 is { } v && v * r.Close >= settings.MinDollarVolume)
            .Where(r => r is { Ema50: { } fast, Ema200: { } slow, High250: > 0 } && fast > slow && r.Close > slow
                        && r.Close >= r.High250.Value * (1 - settings.MaxBelowHigh))
            .OrderByDescending(r => r.Close / r.High250!.Value)
            .ThenByDescending(r => r.AverageVolume50!.Value * r.Close)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .DistinctBy(r => r.Symbol, StringComparer.Ordinal)
            .Take(settings.ShortlistSize)
            .ToList();
    }

    /// <summary>
    /// A shortlisted stock against the advisor's entry rule on its own bars: a candidate when it breaks out, or is in an uptrend
    /// within <see cref="ScreenerSettings.NearHighFraction"/> of its prior closing high; sized on <paramref name="capital"/>
    /// by the advisor's rules. Null otherwise.
    /// </summary>
    public static Candidate? Evaluate(
        string symbol, string? name, string? venue, decimal? marketCap, IReadOnlyList<Candle> bars, DateOnly today, decimal capital,
        AdvisorSettings advisor, ScreenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(settings);
        var entry = Advisor.Entry(bars, today, 1m, advisor);
        if (entry is not { Close: { } close, PriorHigh: { } high, SlowAverage: { } slow, Atr: { } atr })
            return null;
        var near = entry.State == EntryState.Uptrend && high <= close * (1 + settings.NearHighFraction);
        if (entry.State != EntryState.Breakout && !near)
            return null;

        var stop = Advisor.InitialStop(close, atr, 0.01m, advisor);
        if (stop <= 0)
            return null;
        var shares = Advisor.FloorTo(Advisor.Sized(capital, close, stop, atr, 1m, advisor), 1m);
        var from = bars[Math.Max(0, bars.Count - 1 - advisor.MomentumDays)].Close;
        return new(symbol, name, venue, TrendIndicators.TradingDay(bars[^1]), entry.State, close, high, Math.Round(slow, 4, MidpointRounding.ToEven),
            Math.Round(atr, 4, MidpointRounding.ToEven), stop, shares, Math.Round(Indicators.Change(from, close) ?? 0m, 4, MidpointRounding.ToEven), marketCap);
    }

    /// <summary>The candidates in the order they are listed: entry signals first, then by momentum, at most <see cref="ScreenerSettings.MaxCandidates"/>.</summary>
    public static IReadOnlyList<Candidate> Rank(IEnumerable<Candidate> candidates, ScreenerSettings settings) =>
        candidates
            .OrderBy(c => c.State == EntryState.Breakout ? 0 : 1)
            .ThenByDescending(c => c.Momentum)
            .ThenBy(c => c.Symbol, StringComparer.Ordinal)
            .Take(settings.MaxCandidates)
            .ToList();
}
