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

    public RiskLimits ToRiskLimits() => new(
        AllocationQuote,
        MaxOrderQuote,
        MaxPositionQuotePerSymbol,
        MaxDailyLossQuote,
        MaxDrawdownFraction,
        MaxOrdersPerMinute,
        MaxPriceDeviationFraction,
        AllowedSymbols.ToHashSet(StringComparer.Ordinal));
}
