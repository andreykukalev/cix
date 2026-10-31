using System.Text.Json;
using System.Text.Json.Serialization;
using Cix.Domain.Exchange;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Host;

/// <summary>Reason codes the host adds to the advisor's notes.</summary>
public static class HostReasons
{
    /// <summary>The day's EODHD calls were spent: the symbol keeps its older bars, if any.</summary>
    public const string CallBudget = "call-budget";

    /// <summary>EODHD refused or failed the request: the symbol keeps its older bars, if any.</summary>
    public const string DataError = "data-error";

    /// <summary>A split or dividend re-adjusted the history: the held position was moved into the new terms.</summary>
    public const string Adjusted = "adjusted";
}

/// <summary>One daily bar as stored: the trading day and its prices, adjusted to the terms of the fetch.</summary>
public sealed record StoredBar(DateOnly Day, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)
{
    public static StoredBar From(Candle c) => new(TrendIndicators.TradingDay(c), c.Open, c.High, c.Low, c.Close, c.Volume);

    public Candle ToCandle() => new(new DateTimeOffset(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Open, High, Low, Close, Volume);
}

/// <summary>A symbol's bars from its latest fetch, so a later run that day spends no call on it.</summary>
public sealed record CachedBars(string Symbol, DateTimeOffset FetchedAt, IReadOnlyList<StoredBar> Bars);

/// <summary>A close saved with the portfolio, to recognise a split or dividend by how the bars' terms moved since.</summary>
public sealed record StoredClose(DateOnly Day, decimal Close);

/// <summary>Where a held or advised symbol's bars stood at the last run: its last bar, and closes before it.</summary>
public sealed record SymbolTerms(DateOnly LastBarDay, IReadOnlyList<StoredClose> References);

/// <summary>
/// The paper portfolio between runs: its cash and positions, the advice of the last close (carried out at the next open),
/// the EODHD calls spent (or reserved) on <see cref="CallsDay"/>, and the terms of each held or advised symbol's bars.
/// </summary>
/// <param name="Capital">The starting cash, shown as the allocation.</param>
public sealed record PaperState(
    string BaseCurrency,
    decimal Capital,
    decimal Cash,
    IReadOnlyList<PaperPosition> Positions,
    DateOnly? LastAdvisedDay,
    IReadOnlyList<Recommendation> Pending,
    DateOnly? CallsDay,
    int Calls,
    IReadOnlyDictionary<string, SymbolTerms>? Terms = null)
{
    public static PaperState Start(StocksConfig config) => new(config.BaseCurrency, config.Capital, config.Capital, [], null, [], null, 0);
}

/// <summary>A stored value and its version, for a write that fails when another run changed it meanwhile.</summary>
public sealed record Versioned<T>(T Value, string ETag);

/// <summary>Every document's shape: the id (also the partition key) and the value.</summary>
public sealed record Envelope<T>(string Id, T Value);

/// <summary>The advice the dashboard's Stocks tab reads (stocks/README.md has the contract): plain values, no enums.</summary>
public sealed record AdviceDocument(
    DateOnly AsOf,
    DateTimeOffset GeneratedAt,
    string Regime,
    string Benchmark,
    string BaseCurrency,
    decimal Capital,
    decimal Cash,
    decimal Equity,
    IReadOnlyList<ActionLine> Actions,
    IReadOnlyList<HoldingLine> Holdings,
    IReadOnlyList<NoteLine> Notes,
    IReadOnlyList<TradeLine> Trades,
    IReadOnlyList<SkippedLine> Skipped);

public sealed record ActionLine(string Kind, string Symbol, string? Name, string? Currency, decimal Quantity, decimal ReferencePrice, decimal? Stop, string Reason);

public sealed record HoldingLine(string Symbol, string? Name, string? Currency, decimal Quantity, decimal AverageCost, decimal? LastClose, decimal? Stop, DateOnly OpenedOn);

public sealed record NoteLine(string Symbol, string Reason);

/// <summary>A paper fill of this run, carried out at the open after the previous advice.</summary>
public sealed record TradeLine(DateOnly Day, string Symbol, string Kind, decimal Quantity, decimal Price, decimal FeesBase, decimal? ProfitBase, string Reason);

public sealed record SkippedLine(DateOnly Day, string Symbol, string Kind, string Reason);

public static class StocksJson
{
    /// <summary>
    /// Every number is written as a string and read back from one, so decimals keep their exact value (Cosmos DB would store
    /// JSON numbers as doubles; CLAUDE.md rule 7). Enums are kebab-case words: <c>set-stop</c>, <c>risk-on</c>.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    public static string Word<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
