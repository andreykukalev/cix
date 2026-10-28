namespace Cix.Domain.Risk;

/// <summary>Configured risk limits. Validated against <see cref="HardCeilings"/> at startup.</summary>
/// <param name="AllowedSymbols">Symbols that are always permitted, and tradable until the owner changes the list at run time.</param>
/// <param name="AllowedQuoteCurrency">
/// When set, the owner may also add any pair quoted in this currency at run time (see <see cref="TradableSymbols"/>), up to
/// <paramref name="MaxTradableSymbols"/> pairs in all. When null, only <paramref name="AllowedSymbols"/> are permitted.
/// </param>
/// <param name="MaxTradableSymbols">The most pairs tradable at once when <paramref name="AllowedQuoteCurrency"/> is set; 0 otherwise.</param>
/// <param name="MaxEmergencySlippageFraction">
/// How far below the reference price an engine stop-loss sell may be priced (<see cref="OrderPurpose.ProtectiveExit"/>).
/// Null means <paramref name="MaxPriceDeviationFraction"/>, the band every other order gets. Never above
/// <see cref="HardCeilings.MaxPriceDeviationFraction"/>.
/// </param>
public sealed partial record RiskLimits(
    decimal AllocationQuote,
    decimal MaxOrderQuote,
    decimal MaxPositionQuotePerSymbol,
    decimal MaxDailyLossQuote,
    decimal MaxDrawdownFraction,
    int MaxOrdersPerMinute,
    decimal MaxPriceDeviationFraction,
    IReadOnlySet<string> AllowedSymbols,
    string? AllowedQuoteCurrency = null,
    int MaxTradableSymbols = 0,
    decimal? MaxEmergencySlippageFraction = null)
{
    /// <summary>The price band for the engine's stop-loss sells: <see cref="MaxEmergencySlippageFraction"/>, or the normal band.</summary>
    public decimal EmergencySlippageFraction => MaxEmergencySlippageFraction ?? MaxPriceDeviationFraction;

    /// <summary>The most pairs a run-time list may hold.</summary>
    public const int MaxTradableSymbolsLimit = 20;

    /// <summary>How many pairs may be tradable at once.</summary>
    public int TradableCap => AllowedQuoteCurrency is null ? AllowedSymbols.Count : MaxTradableSymbols;

    /// <summary>
    /// Whether <paramref name="symbol"/> (upper-case <c>BASE-QUOTE</c>) may ever be traded under these limits: it is in
    /// <see cref="AllowedSymbols"/>, or a pair in <see cref="AllowedQuoteCurrency"/> when that is set.
    /// </summary>
    public bool Permits(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (AllowedSymbols.Contains(symbol))
            return true;
        if (AllowedQuoteCurrency is null)
            return false;
        var match = SymbolFormat().Match(symbol);
        return match.Success && string.Equals(match.Groups[2].Value, AllowedQuoteCurrency, StringComparison.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^([A-Z0-9]{1,20})-([A-Z]{3,5})$")]
    private static partial System.Text.RegularExpressions.Regex SymbolFormat();

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
        if (MaxEmergencySlippageFraction is { } emergency && (emergency <= 0 || emergency > HardCeilings.MaxPriceDeviationFraction))
            errors.Add($"MaxEmergencySlippageFraction must be in (0, {HardCeilings.MaxPriceDeviationFraction}].");
        if (AllowedSymbols.Count == 0)
            errors.Add("AllowedSymbols must not be empty.");
        if (AllowedQuoteCurrency is null)
        {
            if (MaxTradableSymbols != 0)
                errors.Add("MaxTradableSymbols needs AllowedQuoteCurrency.");
        }
        else
        {
            if (!QuoteFormat().IsMatch(AllowedQuoteCurrency))
                errors.Add("AllowedQuoteCurrency must be 3 to 5 upper-case letters, for example EUR.");
            else if (AllowedSymbols.Any(s => !s.EndsWith("-" + AllowedQuoteCurrency, StringComparison.Ordinal)))
                errors.Add("AllowedSymbols must all be quoted in AllowedQuoteCurrency.");
            if (MaxTradableSymbols < AllowedSymbols.Count || MaxTradableSymbols > MaxTradableSymbolsLimit)
                errors.Add($"MaxTradableSymbols must be in [the number of AllowedSymbols, {MaxTradableSymbolsLimit}].");
        }

        return errors;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z]{3,5}$")]
    private static partial System.Text.RegularExpressions.Regex QuoteFormat();
}
