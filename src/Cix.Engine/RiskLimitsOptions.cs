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
    /// The limits from the owner's file at <paramref name="path"/> alone. Throws if <paramref name="other"/> (the engine's
    /// configuration from every other source) holds any <c>Risk</c> setting: an environment variable such as
    /// <c>Risk__AllowedQuoteCurrency</c> must never widen what the file permits.
    /// </summary>
    public static RiskLimits Load(string path, IConfiguration? other = null)
    {
        if (other?.GetSection("Risk").Exists() == true)
            throw new InvalidOperationException("Risk settings come only from config/risk-limits.json; remove them from the environment, the command line and appsettings.");
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
        return file.GetSection("Risk").Get<RiskLimitsOptions>()?.ToRiskLimits()
            ?? throw new InvalidOperationException("Missing 'Risk' section in risk-limits.json.");
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
