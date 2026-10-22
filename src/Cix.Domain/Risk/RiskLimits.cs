namespace Cix.Domain.Risk;

/// <summary>Configured risk limits. Validated against <see cref="HardCeilings"/> at startup.</summary>
public sealed record RiskLimits(
    decimal AllocationQuote,
    decimal MaxOrderQuote,
    decimal MaxPositionQuotePerSymbol,
    decimal MaxDailyLossQuote,
    decimal MaxDrawdownFraction,
    int MaxOrdersPerMinute,
    decimal MaxPriceDeviationFraction,
    IReadOnlySet<string> AllowedSymbols)
{
    /// <summary>Returns the problems with these limits; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (AllocationQuote <= 0 || AllocationQuote > HardCeilings.MaxAllocationQuote)
            errors.Add($"AllocationQuote must be in (0, {HardCeilings.MaxAllocationQuote}].");
        if (MaxOrderQuote <= 0 || MaxOrderQuote > AllocationQuote * HardCeilings.MaxOrderFractionOfAllocation)
            errors.Add($"MaxOrderQuote must be in (0, {HardCeilings.MaxOrderFractionOfAllocation:P0} of the allocation].");
        if (MaxPositionQuotePerSymbol <= 0 || MaxPositionQuotePerSymbol > AllocationQuote)
            errors.Add("MaxPositionQuotePerSymbol must be in (0, AllocationQuote].");
        if (MaxDailyLossQuote <= 0 || MaxDailyLossQuote > AllocationQuote * HardCeilings.MaxDailyLossFractionOfAllocation)
            errors.Add($"MaxDailyLossQuote must be in (0, {HardCeilings.MaxDailyLossFractionOfAllocation:P0} of the allocation].");
        if (MaxDrawdownFraction <= 0 || MaxDrawdownFraction > HardCeilings.MaxDrawdownFraction)
            errors.Add($"MaxDrawdownFraction must be in (0, {HardCeilings.MaxDrawdownFraction}].");
        if (MaxOrdersPerMinute <= 0 || MaxOrdersPerMinute > HardCeilings.MaxOrdersPerMinute)
            errors.Add($"MaxOrdersPerMinute must be in (0, {HardCeilings.MaxOrdersPerMinute}].");
        if (MaxPriceDeviationFraction <= 0 || MaxPriceDeviationFraction > HardCeilings.MaxPriceDeviationFraction)
            errors.Add($"MaxPriceDeviationFraction must be in (0, {HardCeilings.MaxPriceDeviationFraction}].");
        if (AllowedSymbols.Count == 0)
            errors.Add("AllowedSymbols must not be empty.");

        return errors;
    }
}
