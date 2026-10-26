using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cix.Domain.Risk;
using Cix.Domain.Scanning;
using Cix.Engine.Scanning;
using Cix.Engine.Tools;

namespace Cix.Engine.Strategy;

/// <summary>
/// One strategy parameter as the owner's dashboard shows it: what it means in plain words, its valid range, and whether a
/// change affects only new trades or open positions too. Ranges already include the mandatory limit that caps them.
/// </summary>
/// <param name="Key">Its path in the spec's parameters: <c>orderQuote</c>, or <c>scanner.stopAtr</c>.</param>
/// <param name="Group"><c>trend</c>, <c>entry</c>, <c>size</c>, <c>stops</c>, <c>exposure</c>, <c>frequency</c>, <c>exits</c>, <c>filters</c>, <c>costs</c> or <c>ranking</c>.</param>
/// <param name="Kind"><c>number</c>, <c>integer</c>, <c>boolean</c> or <c>text</c>.</param>
/// <param name="Unit"><c>EUR</c>, <c>fraction</c> (0.01 = 1%), <c>ATR</c> (multiples of the hourly average true range), <c>minutes</c>, <c>candles</c>, <c>ratio</c>, <c>weight</c>, <c>count</c>, or null.</param>
/// <param name="Applies"><c>new-trades</c>, or <c>open-positions-too</c> for the trend and exit rules, which every held position follows from the next scan.</param>
/// <param name="BoundBy">The mandatory limit that caps <paramref name="Max"/>, when one does (a key of <see cref="MandatoryLimit"/>).</param>
public sealed record StrategyParameter(
    string Key,
    string Group,
    string Label,
    string Explanation,
    string Kind,
    decimal? Min,
    decimal? Max,
    string? Unit,
    string Applies,
    string? BoundBy = null);

/// <summary>A safety limit no strategy can change: from the owner-only <c>config/risk-limits.json</c>, or fixed in the engine.</summary>
/// <param name="Source"><c>risk-limits</c> (owner-only file) or <c>engine</c> (fixed in code).</param>
public sealed record MandatoryLimit(string Key, string Label, string Explanation, string Value, string Source);

/// <summary>What the dashboard needs to explain and check a strategy: its parameters, the mandatory limits, and the tools a spec may allow.</summary>
public sealed record StrategySchemaDocument(
    string Mode,
    DateTimeOffset WrittenAt,
    IReadOnlyList<StrategyParameter> Parameters,
    IReadOnlyList<MandatoryLimit> Mandatory,
    IReadOnlyList<string> AllowedTools);

/// <summary>
/// The parameters a trading strategy can set, their meaning and ranges, and the checks a spec must pass before the engine
/// activates it. The schema and the checks come from the same list, so what the dashboard explains is what the engine enforces.
/// Strategy limits only ever tighten the mandatory ones: the risk gate stays final whatever a spec says.
/// </summary>
public static partial class StrategySchema
{
    public const string Scanner = "scanner";

    /// <summary>Tools a trading spec may allow. Analyst, operator and owner-request tools are never a spec's to grant.</summary>
    public static readonly IReadOnlyList<string> TradingTools =
    [
        "get_strategy_spec", "get_market_view", "get_features", "get_portfolio", "get_position_plans", "get_open_orders",
        "scan_market", "place_order", "cancel_order",
    ];

    public const int MaxInstructions = 20_000;
    public const int MaxText = 500;

    /// <summary>Every parameter, with ranges capped by <paramref name="limits"/>.</summary>
    public static IReadOnlyList<StrategyParameter> Parameters(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        const string New = "new-trades";
        const string All = "open-positions-too";
        return
        [
            // Trend indicators
            new("scanner.fastEmaPeriod", "trend", "Fast EMA period",
                "The shorter exponential moving average of hourly closing prices, in hours. A smaller number reacts sooner to price changes, and also to noise.",
                "integer", 3, ScannerParameters.MaxEmaPeriod - 1, "candles", All),
            new("scanner.slowEmaPeriod", "trend", "Slow EMA period",
                "The longer moving average. The trend is up when the fast EMA is above the slow one and down when it is below. This decides which coins can be bought and, with trend exits on, when held coins are sold.",
                "integer", 4, ScannerParameters.MaxEmaPeriod, "candles", All),

            // Entry conditions
            new("scanner.requireUptrendPullback", "entry", "Buy only pullbacks in an uptrend",
                "When on, a coin is bought only in an uptrend, close to its fast EMA, and after an hour that didn't fall. When off, any coin that passes the filters can be bought, which is for testing orders rather than for trading well.",
                "boolean", null, null, null, New),
            new("scanner.entryMaxAboveEma20Atr", "entry", "Furthest above the fast EMA",
                "How far above the fast EMA the price may be at entry, in ATRs. Lower waits for a deeper pullback, so fewer but less stretched entries.",
                "number", 0, 3, "ATR", New),

            // Position size
            new("orderQuote", "size", "Largest order",
                "The most one buy may spend. The scanner sizes each buy as the smaller of this and the size whose loss at the stop equals the risk per trade, so most orders are smaller. The engine reduces a larger buy to this.",
                "number", 1, limits.MaxOrderQuote, "EUR", New, "maxOrderQuote"),
            new("scanner.riskPerTradeQuote", "size", "Risk per trade",
                "What one trade should lose if it is stopped out, fees and slippage included. With a wider stop the position is smaller, so each trade risks about the same.",
                "number", 0.1m, limits.MaxOrderQuote, "EUR", New),

            // Stop-loss and take-profit
            new("scanner.stopAtr", "stops", "Stop-loss distance",
                "How far below the entry the stop-loss sits, in ATRs. The engine sells by itself when the bid reaches it. Wider stops are hit less often but lose more when they are.",
                "number", 0.5m, 5, "ATR", New),
            new("scanner.rewardRisk", "stops", "Take-profit distance",
                "The take-profit, as a multiple of the stop distance (reward to risk). 2 means the target is twice as far above the entry as the stop is below it. A further target is reached less often.",
                "number", 0.5m, 5, "ratio", New),

            // Exposure
            new("maxPositionQuotePerSymbol", "exposure", "Most in one coin",
                "The most the strategy holds in a single coin, counting buys still working. A buy that would go over is reduced or refused.",
                "number", 1, limits.MaxPositionQuotePerSymbol, "EUR", New, "maxPositionQuotePerSymbol"),
            new("maxExposureFraction", "exposure", "Most of the allocation in coins",
                "The share of the allocation that may be held in coins at once; the rest stays in cash. 0.5 means at most half.",
                "number", 0.05m, 1, "fraction", New, "allocationQuote"),
            new("dailyLossStopQuote", "exposure", "Daily loss stop",
                "Once today's loss reaches this, the strategy makes no new buys until tomorrow (UTC). Sells, stops and targets carry on.",
                "number", 0.5m, limits.MaxDailyLossQuote, "EUR", New, "maxDailyLossQuote"),

            // Trading frequency
            new("maxNewPositionsPerRun", "frequency", "New positions per run",
                "How many buys one trader run may place. The engine refuses any beyond this.",
                "integer", 0, 5, "count", New),
            new("scanner.maxEntrySignals", "frequency", "Buy signals per scan",
                "How many coins the scanner suggests buying at once, best ranked first.",
                "integer", 0, 10, "count", New),
            new("entryCooldownMinutes", "frequency", "Cooldown between buys",
                "The least time between two buys. While it runs the scanner suggests no buys and the engine refuses them. 0 means no cooldown.",
                "integer", 0, 1440, "minutes", New),

            // Exit rules
            new("scanner.trendExits", "exits", "Sell when the trend turns down",
                "When on, a held coin is sold once its trend turns down, even before its stop. When off, positions leave only at their stop or target. This applies to coins already held, from the next scan.",
                "boolean", null, null, null, All),

            // Coin filters
            new("scanner.minQuoteVolume24h", "filters", "Least 24h volume",
                "A coin must have traded at least this much in the last 24 hours, so orders fill near the quoted price.",
                "number", 0, 10_000_000, "EUR", New),
            new("scanner.maxSpreadFraction", "filters", "Widest spread",
                "The largest gap between the best buy and sell prices, as a share of the price. Wider spreads cost more on every trade.",
                "number", 0.0001m, 0.02m, "fraction", New),
            new("scanner.maxAtrFraction", "filters", "Most volatile",
                "Coins whose hourly ATR is above this share of the price are skipped as too wild for the stop sizes used.",
                "number", 0.005m, 0.2m, "fraction", New),
            new("scanner.minAtrToCostRatio", "filters", "Least volatility for the costs",
                "A coin's hourly ATR must be at least this multiple of a round trip's fees, spread and slippage, so a normal move can pay for the trade.",
                "number", 0.1m, 10, "ratio", New),
            new("scanner.minHourlyCandles24h", "filters", "Least trading hours",
                "Hourly candles needed in the last 24 hours to judge a coin at all.",
                "integer", 1, 24, "candles", New),

            // Cost assumptions
            new("scanner.takerFeeRate", "costs", "Fee rate",
                "The exchange fee per side the scanner assumes when it weighs costs. It doesn't change what the exchange charges.",
                "number", 0, 0.01m, "fraction", New),
            new("scanner.slippageFraction", "costs", "Slippage allowance",
                "How much worse than the quote a fill is assumed to be, per side.",
                "number", 0, 0.01m, "fraction", New),

            // Ranking
            new("scanner.momentumWeight", "ranking", "Momentum weight",
                "How much the last 24 hours' move counts when coins are ranked.", "number", 0, 5, "weight", New),
            new("scanner.trendWeight", "ranking", "Trend weight",
                "How much the trend direction counts in the ranking.", "number", 0, 5, "weight", New),
            new("scanner.volumeWeight", "ranking", "Volume weight",
                "How much unusual trading volume counts in the ranking.", "number", 0, 5, "weight", New),
            new("scanner.costWeight", "ranking", "Cost weight",
                "How much trading costs count against a coin in the ranking.", "number", 0, 10, "weight", New),
        ];
    }

    /// <summary>The safety limits every strategy runs under, which only the owner can change.</summary>
    public static IReadOnlyList<MandatoryLimit> Mandatory(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        static string Inv(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        return
        [
            new("allocationQuote", "Allocation", "The most the engine trades with in total, cash and coins together.", Inv(limits.AllocationQuote), "risk-limits"),
            new("maxOrderQuote", "Largest order", "No single order may be worth more, whatever the strategy says.", Inv(limits.MaxOrderQuote), "risk-limits"),
            new("maxPositionQuotePerSymbol", "Most in one coin", "No coin may be held above this value.", Inv(limits.MaxPositionQuotePerSymbol), "risk-limits"),
            new("maxDailyLossQuote", "Daily loss limit", "Once today's loss reaches this, all new buys stop until tomorrow (UTC).", Inv(limits.MaxDailyLossQuote), "risk-limits"),
            new("maxDrawdownFraction", "Drawdown limit", "A fall of this share from the highest equity stops new buys until the owner resumes trading.", Inv(limits.MaxDrawdownFraction), "risk-limits"),
            new("maxOrdersPerMinute", "Orders per minute", "The most orders of any kind in one minute.", Inv(limits.MaxOrdersPerMinute), "risk-limits"),
            new("maxPriceDeviationFraction", "Price band", "An order's limit price must be within this share of the current price.", Inv(limits.MaxPriceDeviationFraction), "risk-limits"),
            new("allowedSymbols", "Tradable coins", "Only these coins can be bought or sold.", string.Join(", ", limits.AllowedSymbols.Order(StringComparer.Ordinal)), "risk-limits"),
            new("minExitDistanceFraction", "Least stop and target distance", "A buy's stop and target must be at least this share away from its price.", Inv(MarketScanner.MinExitDistanceFraction), "engine"),
            new("protectiveExits", "Stops and targets watched", "The engine sells by itself when a position reaches its stop or target, between agent runs too.", "on", "engine"),
        ];
    }

    public static StrategySchemaDocument Document(RiskLimits limits, TradingMode mode, DateTimeOffset at) =>
        new(mode.ToString().ToLowerInvariant(), at, Parameters(limits), Mandatory(limits), TradingTools);

    /// <summary>Top-level keys that aren't numbers or booleans: checked on their own.</summary>
    private static readonly IReadOnlySet<string> OtherTopLevel = new HashSet<string>(StringComparer.Ordinal) { "timeframe", "symbols", Scanner };

    /// <summary>
    /// The problems that stop <paramref name="spec"/> from being activated from the database; empty when it may run in
    /// <paramref name="mode"/>. Checks its shape, every parameter against its range and the mandatory limits, and the scanner
    /// settings exactly as the scanner will read them.
    /// </summary>
    public static IReadOnlyList<string> Validate(StrategySpec spec, RiskLimits limits, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(limits);
        var errors = new List<string>();

        if (spec.Name is null || !NamePattern().IsMatch(spec.Name))
            errors.Add("name must be 1 to 40 lowercase letters, digits or dashes.");
        if (spec.Version is < 1 or > 9999)
            errors.Add("version must be between 1 and 9999.");
        // Live status is the owner's to set, in the repository's files: a database document can't promote itself (#105).
        if (spec.Status != SpecStatus.Paper)
            errors.Add($"status must be paper (a {spec.Status.ToString().ToLowerInvariant()} spec can't be activated from the database).");
        if (mode != TradingMode.Paper)
            errors.Add("strategies are switched from the database in paper trading only, until live switching is built (#71).");
        if (string.IsNullOrWhiteSpace(spec.Instructions) || spec.Instructions.Length > MaxInstructions)
            errors.Add($"instructions are required, at most {MaxInstructions} characters.");
        if (spec.AllowedTools is null || spec.AllowedTools.Count == 0)
            errors.Add("allowedTools must list at least one tool.");
        else if (spec.AllowedTools.FirstOrDefault(t => !TradingTools.Contains(t, StringComparer.Ordinal)) is { } tool)
            errors.Add($"allowedTools may only list trading tools ({string.Join(", ", TradingTools)}); {Clip(tool)} isn't one.");
        foreach (var (text, what) in new[] { (spec.CreatedBy, "createdBy"), (spec.Purpose, "purpose"), (spec.RiskProfile, "riskProfile") })
        {
            if (text is { Length: > MaxText })
                errors.Add($"{what} must be at most {MaxText} characters.");
        }

        if (spec.Parameters is null)
        {
            errors.Add("parameters are required.");
            return errors;
        }

        var schema = Parameters(limits).ToDictionary(p => p.Key, StringComparer.Ordinal);
        foreach (var (key, value) in spec.Parameters)
        {
            if (OtherTopLevel.Contains(key))
                continue;
            if (!schema.TryGetValue(key, out var parameter))
                errors.Add($"parameters.{Clip(key)} isn't a known parameter.");
            else
                CheckValue(parameter, value, errors);
        }

        if (spec.Parameters.TryGetValue("timeframe", out var timeframe) && !(timeframe.ValueKind == JsonValueKind.String && timeframe.GetString() == "1h"))
            errors.Add("parameters.timeframe must be \"1h\": the scanner works on hourly candles.");
        if (spec.Parameters.TryGetValue("symbols", out var symbols)
            && (symbols.ValueKind != JsonValueKind.Array || symbols.GetArrayLength() > 20 || symbols.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String)))
            errors.Add("parameters.symbols must be a list of at most 20 symbols.");

        if (spec.Parameters.TryGetValue(Scanner, out var scanner))
        {
            if (scanner.ValueKind != JsonValueKind.Object)
                errors.Add("parameters.scanner must be an object.");
            else
            {
                foreach (var property in scanner.EnumerateObject())
                {
                    if (!schema.TryGetValue($"{Scanner}.{property.Name}", out var parameter))
                        errors.Add($"parameters.scanner.{Clip(property.Name)} isn't a known parameter.");
                    else
                        CheckValue(parameter, property.Value, errors);
                }
            }
        }

        if (errors.Count > 0)
            return errors;

        // The scanner's own reading: exactly what will run, with its cross-checks (fast EMA below slow, and so on).
        ScannerParameters? scan = null;
        try
        {
            scan = MarketScanService.FromSpec(spec, new ScannerParameters());
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }

        if (spec.Allows("place_order") && !spec.Parameters.ContainsKey("orderQuote"))
            errors.Add("parameters.orderQuote is required when the spec allows place_order.");
        if (scan is not null && spec.Parameters.ContainsKey("orderQuote") && scan.RiskPerTradeQuote > scan.OrderQuote)
            errors.Add("parameters.scanner.riskPerTradeQuote can't be more than orderQuote.");
        return errors;
    }

    /// <summary>The strategy's own limits for the engine's buys (<see cref="StrategyGate"/>); none for a spec without them.</summary>
    public static StrategyLimits LimitsOf(StrategySpec? spec, RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (spec?.Parameters is not { } p)
            return StrategyLimits.None;
        decimal? Number(string key) => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= 0 ? d : null;
        int? Integer(string key) => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i : null;
        return new StrategyLimits(
            Number("orderQuote"),
            Number("maxPositionQuotePerSymbol"),
            Number("maxExposureFraction") is { } fraction ? Math.Min(fraction, 1m) * limits.AllocationQuote : null,
            Number("dailyLossStopQuote"),
            Integer("maxNewPositionsPerRun"),
            Integer("entryCooldownMinutes") is { } minutes and > 0 ? TimeSpan.FromMinutes(minutes) : null);
    }

    private static void CheckValue(StrategyParameter parameter, JsonElement value, List<string> errors)
    {
        var name = $"parameters.{parameter.Key}";
        switch (parameter.Kind)
        {
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    errors.Add($"{name} must be true or false.");
                return;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var whole))
                    errors.Add($"{name} must be a whole number.");
                else
                    CheckRange(parameter, whole, errors);
                return;
            default:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    errors.Add($"{name} must be a number.");
                else
                    CheckRange(parameter, number, errors);
                return;
        }
    }

    private static void CheckRange(StrategyParameter parameter, decimal value, List<string> errors)
    {
        if (value < parameter.Min || value > parameter.Max)
        {
            var bound = parameter.BoundBy is null ? string.Empty : $" (the maximum is the mandatory {parameter.BoundBy})";
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"parameters.{parameter.Key} is {value}; it must be between {parameter.Min} and {parameter.Max}{bound}."));
        }
    }

    /// <summary>Text from a document, made plain and short before it goes into an error message, the journal or the status.</summary>
    internal static string Clip(string? text)
    {
        var plain = new string((text ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' ? c : '?').ToArray());
        return plain.Length <= 40 ? plain : plain[..40] + "...";
    }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex NamePattern();
}
