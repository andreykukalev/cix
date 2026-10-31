using System.Text.Json;
using System.Text.Json.Serialization;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Host;

/// <summary>One instrument the owner watches.</summary>
/// <param name="Theme">A shared risk, for <see cref="AdvisorSettings.MaxThemeFraction"/>: for example <c>semis</c>.</param>
public sealed record WatchItem(string Symbol, string Name, string Currency, string? Theme = null, decimal QuantityStep = 1m)
{
    public Instrument ToInstrument() => new(Symbol, Name, Currency, InstrumentKind.Stock, QuantityStep, Theme: Theme);
}

/// <summary>
/// The daily run's settings, from <c>stocks/config/stocks.json</c> (baked into the image). No secrets: the EODHD key and the
/// database come from the environment.
/// </summary>
public sealed record StocksConfig
{
    public required string BaseCurrency { get; init; }

    /// <summary>The paper portfolio's starting cash, in the base currency. Read once, when there is no saved portfolio yet.</summary>
    public required decimal Capital { get; init; }

    /// <summary>The market regime's index, for example <c>SPY.US</c>.</summary>
    public required string Benchmark { get; init; }

    public required IReadOnlyList<WatchItem> Watchlist { get; init; }

    /// <summary>Units of the base currency per unit of another currency, fixed: needed only for instruments in another currency.</summary>
    public IReadOnlyDictionary<string, decimal> Fx { get; init; } = new Dictionary<string, decimal>();

    /// <summary>How far back each fetch reaches, in calendar days. EODHD's free key gives one year: about 250 bars.</summary>
    public int HistoryDays { get; init; } = 365;

    /// <summary>EODHD calls one run may spend. Every fetch is one call, whatever its range.</summary>
    public int MaxCallsPerRun { get; init; } = 18;

    /// <summary>EODHD calls all runs of one UTC day may spend together, under the key's daily limit (20 on the free key).</summary>
    public int MaxCallsPerDay { get; init; } = 18;

    /// <summary>
    /// When EODHD has a session's bars, as a UTC time on the next day: bars fetched after it are reused by every later run
    /// until the next session's, so a repeated run spends no calls. 01:00 is four hours or more after the US close.
    /// </summary>
    public TimeOnly DataReadyUtc { get; init; } = new(1, 0);

    public AdvisorSettings Advisor { get; init; } = new();

    public SimulationSettings Costs { get; init; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelt setting fails instead of being ignored
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static StocksConfig Load(string path)
    {
        using var file = File.OpenRead(path);
        var config = JsonSerializer.Deserialize<StocksConfig>(file, Json) ?? throw new ArgumentException($"{path} is empty.");
        return config.Validate();
    }

    /// <summary>The advisor's settings, with the benchmark from this file.</summary>
    public AdvisorSettings AdvisorSettings => Advisor with { BenchmarkSymbol = Benchmark };

    public StocksConfig Validate()
    {
        if (BaseCurrency is not { Length: 3 } || !BaseCurrency.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"Base currency '{BaseCurrency}' isn't a three-letter code.");
        if (Capital <= 0 || Capital > 1_000_000m)
            throw new ArgumentOutOfRangeException(nameof(Capital), Capital, "Must be above 0 and at most 1 000 000.");
        if (Watchlist is not { Count: > 0 and <= 100 })
            throw new ArgumentException("The watchlist needs 1 to 100 instruments.");
        var symbols = new HashSet<string>(StringComparer.Ordinal) { Benchmark };
        foreach (var item in Watchlist)
        {
            if (!StockSymbol.IsValid(item.Symbol))
                throw new ArgumentException($"Watchlist symbol '{item.Symbol}' isn't TICKER.EXCHANGE.");
            if (!symbols.Add(item.Symbol))
                throw new ArgumentException($"{item.Symbol} is listed twice, or is the benchmark.");
            if (item.Currency is not { Length: 3 } || !item.Currency.All(char.IsAsciiLetterUpper))
                throw new ArgumentException($"{item.Symbol}'s currency '{item.Currency}' isn't a three-letter code.");
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80)
                throw new ArgumentException($"{item.Symbol} needs a name of at most 80 characters.");
            if (item.Theme is { } theme && (theme.Length is 0 or > 40 || !theme.All(c => char.IsAsciiLetterLower(c) || c == '-')))
                throw new ArgumentException($"{item.Symbol}'s theme '{theme}' must be lower-case letters and hyphens.");
            if (item.QuantityStep is <= 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(Watchlist), item.QuantityStep, $"{item.Symbol}'s quantity step must be above 0 and at most 1.");
        }

        foreach (var (currency, rate) in Fx)
        {
            if (rate <= 0)
                throw new ArgumentOutOfRangeException(nameof(Fx), rate, $"The {currency} rate must be positive.");
        }

        if (MaxCallsPerRun is < 1 or > 1000 || MaxCallsPerDay is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaxCallsPerRun), "Call budgets must be positive (at most 1000 a run).");
        if (DataReadyUtc > new TimeOnly(13, 0))
            throw new ArgumentOutOfRangeException(nameof(DataReadyUtc), DataReadyUtc, "Must be before the next US open (13:00 UTC at the latest).");
        var settings = AdvisorSettings.Validate();
        // MinBars trading days need about 1.45 times as many calendar days, with a margin for holidays.
        if (HistoryDays < (settings.MinBars * 3 / 2) + 10 || HistoryDays > 20_000)
            throw new ArgumentOutOfRangeException(nameof(HistoryDays), HistoryDays, $"Too short for the advisor's {settings.MinBars} bars, or over 20 000.");
        Costs.Validate();
        return this;
    }

    /// <summary>
    /// The latest moment, at or before <paramref name="now"/>, from which a session's bars are complete: <see cref="DataReadyUtc"/>
    /// on the day after a weekday. Cached bars fetched after it are as new as EODHD has.
    /// </summary>
    public DateTimeOffset DataCutoff(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        for (var k = 0; k < 8; k++)
        {
            var d = day.AddDays(-k);
            var at = new DateTimeOffset(d.ToDateTime(DataReadyUtc), TimeSpan.Zero);
            if (at <= now && d.AddDays(-1).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                return at;
        }

        throw new InvalidOperationException("Unreachable: a week always has a weekday.");
    }
}
