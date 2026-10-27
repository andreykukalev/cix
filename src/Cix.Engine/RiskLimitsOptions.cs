using Cix.Domain.Risk;

namespace Cix.Engine;

/// <summary>Binds the "Risk" configuration section.</summary>
public sealed class RiskLimitsOptions
{
    public decimal AllocationQuote { get; set; }
    public decimal MaxOrderQuote { get; set; }
    public decimal MaxPositionQuotePerSymbol { get; set; }
    public decimal MaxDailyLossQuote { get; set; }
    public decimal MaxDrawdownFraction { get; set; }
    public int MaxOrdersPerMinute { get; set; }
    public decimal MaxPriceDeviationFraction { get; set; }
    public List<string> AllowedSymbols { get; set; } = [];

    /// <summary>When set, the owner may add any pair in this quote currency from the dashboard, up to <see cref="MaxTradableSymbols"/>.</summary>
    public string? AllowedQuoteCurrency { get; set; }

    public int MaxTradableSymbols { get; set; }

    /// <summary>
    /// The owner's limits file for a trading mode, next to the engine's binary: <c>risk-limits.json</c> (from
    /// <c>config/risk-limits.json</c>) for paper, and <c>risk-limits.live.json</c> (from <c>config/risk-limits.live.json</c>) for
    /// live, so live trades within its own, smaller allocation and never within paper's. Both are owner-only.
    /// </summary>
    public static string FileFor(TradingMode mode) => mode switch
    {
        TradingMode.Paper => "risk-limits.json",
        TradingMode.Live => "risk-limits.live.json",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown trading mode."),
    };

    /// <summary>
    /// The limits for <paramref name="mode"/> from its file in <paramref name="directory"/>. A missing file stops startup: Live
    /// never falls back to paper's limits.
    /// </summary>
    public static RiskLimits LoadFor(TradingMode mode, string directory, IConfiguration? other = null)
    {
        var path = Path.Combine(directory, FileFor(mode));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(mode == TradingMode.Live
                ? "Live trading needs its own limits in config/risk-limits.live.json, which only the owner writes; it isn't there."
                : $"The risk limits file {FileFor(mode)} is missing.");
        }

        return Load(path, other);
    }

    /// <summary>
    /// The limits from the owner's file at <paramref name="path"/> alone. Throws if <paramref name="other"/> (the engine's
    /// configuration from every other source) holds any <c>Risk</c> setting: an environment variable such as
    /// <c>Risk__AllowedQuoteCurrency</c> must never widen what the file permits.
    /// </summary>
    public static RiskLimits Load(string path, IConfiguration? other = null)
    {
        if (other?.GetSection("Risk").Exists() == true)
            throw new InvalidOperationException("Risk settings come only from the owner's limits file (config/risk-limits.json, or risk-limits.live.json for live); remove them from the environment, the command line and appsettings.");
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
        return file.GetSection("Risk").Get<RiskLimitsOptions>()?.ToRiskLimits()
            ?? throw new InvalidOperationException($"Missing 'Risk' section in {Path.GetFileName(path)}.");
    }

    public RiskLimits ToRiskLimits() => new(
        AllocationQuote,
        MaxOrderQuote,
        MaxPositionQuotePerSymbol,
        MaxDailyLossQuote,
        MaxDrawdownFraction,
        MaxOrdersPerMinute,
        MaxPriceDeviationFraction,
        AllowedSymbols.ToHashSet(StringComparer.Ordinal),
        string.IsNullOrWhiteSpace(AllowedQuoteCurrency) ? null : AllowedQuoteCurrency,
        MaxTradableSymbols);
}
