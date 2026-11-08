namespace Cix.Domain.Risk;

/// <summary>Configured risk limits. Validated against <see cref="HardCeilings"/> at startup.</summary>
/// <param name="AllowedQuoteCurrency">
/// The one rule for which pairs may ever be traded (ADR-0021): any pair quoted in this currency, for example <c>EUR</c>. The owner
/// adds and removes pairs from the dashboard (see <see cref="TradableSymbols"/>); no pair list lives in configuration.
/// </param>
/// <param name="MaxTradableSymbols">The most pairs on the watched list at once, 1 to <see cref="MaxTradableSymbolsLimit"/>.</param>
/// <param name="MaxEmergencySlippageFraction">
/// How far below the reference price an engine stop-loss sell may be priced (<see cref="OrderPurpose.ProtectiveExit"/>).
/// Null means <paramref name="MaxPriceDeviationFraction"/>, the band every other order gets. Never above
/// <see cref="HardCeilings.MaxPriceDeviationFraction"/>.
/// </param>
/// <param name="MaxOrderFraction">
/// The largest order as a share of the capital (<see cref="CapitalOf"/>: the account's value, at most
/// <paramref name="AllocationQuote"/>), so the limit follows deposits and withdrawals (ADR-0030). Null keeps only
/// <paramref name="MaxOrderQuote"/>; with both, the smaller applies.
/// </param>
/// <param name="MaxPositionFractionPerSymbol">The most one coin may hold, as a share of the capital; with <paramref name="MaxPositionQuotePerSymbol"/>, the smaller applies.</param>
/// <param name="MaxDailyLossFraction">
/// The daily loss limit as a share of the capital at the start of the day (UTC); with <paramref name="MaxDailyLossQuote"/>, the smaller applies.
/// </param>
public sealed partial record RiskLimits(
    decimal AllocationQuote,
    decimal MaxOrderQuote,
    decimal MaxPositionQuotePerSymbol,
    decimal MaxDailyLossQuote,
    decimal MaxDrawdownFraction,
    int MaxOrdersPerMinute,
    decimal MaxPriceDeviationFraction,
    string AllowedQuoteCurrency,
    int MaxTradableSymbols,
    decimal? MaxEmergencySlippageFraction = null,
    decimal? MaxOrderFraction = null,
    decimal? MaxPositionFractionPerSymbol = null,
    decimal? MaxDailyLossFraction = null)
{
    /// <summary>
    /// What the limits are measured against (ADR-0030): the account's value, cash and coins, never more than
    /// <see cref="AllocationQuote"/> and never below zero. A deposit raises it, a withdrawal or a loss lowers it.
    /// </summary>
    public decimal CapitalOf(decimal equity) => Math.Clamp(equity, 0m, AllocationQuote);

    /// <summary>True when any limit is a share of the capital.</summary>
    public bool ScalesWithCapital => MaxOrderFraction is not null || MaxPositionFractionPerSymbol is not null || MaxDailyLossFraction is not null;

    /// <summary>The largest order at <paramref name="capital"/>: <see cref="MaxOrderQuote"/>, or less under <see cref="MaxOrderFraction"/>.</summary>
    public decimal MaxOrderAt(decimal capital) => Scaled(MaxOrderQuote, MaxOrderFraction, capital);

    /// <summary>The most one coin may hold at <paramref name="capital"/>.</summary>
    public decimal MaxPositionAt(decimal capital) => Scaled(MaxPositionQuotePerSymbol, MaxPositionFractionPerSymbol, capital);

    /// <summary>The daily loss limit for a day that started with <paramref name="capital"/>.</summary>
    public decimal MaxDailyLossAt(decimal capital) => Scaled(MaxDailyLossQuote, MaxDailyLossFraction, capital);

    private static decimal Scaled(decimal quote, decimal? fraction, decimal capital) =>
        fraction is { } f ? Math.Min(quote, Math.Round(f * Math.Max(0m, capital), 2, MidpointRounding.ToZero)) : quote;

    /// <summary>The price band for the engine's stop-loss sells: <see cref="MaxEmergencySlippageFraction"/>, or the normal band.</summary>
    public decimal EmergencySlippageFraction => MaxEmergencySlippageFraction ?? MaxPriceDeviationFraction;

    /// <summary>
    /// The most pairs a watched list may hold (ADR-0021): what the market-data schedule fits in the public API's budget, with
    /// tickers for every coin and order books only for the active ones.
    /// </summary>
    public const int MaxTradableSymbolsLimit = 50;

    /// <summary>How many pairs may be watched at once.</summary>
    public int TradableCap => MaxTradableSymbols;

    /// <summary>
    /// Whether <paramref name="symbol"/> (upper-case <c>BASE-QUOTE</c>) may ever be traded under these limits: a pair quoted in
    /// <see cref="AllowedQuoteCurrency"/>.
    /// </summary>
    public bool Permits(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
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
        if (AllowedQuoteCurrency is null || !QuoteFormat().IsMatch(AllowedQuoteCurrency))
            errors.Add("AllowedQuoteCurrency must be 3 to 5 upper-case letters, for example EUR.");
        if (MaxTradableSymbols < 1 || MaxTradableSymbols > MaxTradableSymbolsLimit)
            errors.Add($"MaxTradableSymbols must be in [1, {MaxTradableSymbolsLimit}].");
        if (MaxOrderFraction is { } order && (order <= 0 || order > HardCeilings.MaxOrderFractionOfAllocation))
            errors.Add($"MaxOrderFraction must be in (0, {HardCeilings.MaxOrderFractionOfAllocation}].");
        if (MaxPositionFractionPerSymbol is { } position && (position <= 0 || position > 1))
            errors.Add("MaxPositionFractionPerSymbol must be in (0, 1].");
        if (MaxDailyLossFraction is { } loss && (loss <= 0 || loss > HardCeilings.MaxDailyLossFractionOfAllocation))
            errors.Add($"MaxDailyLossFraction must be in (0, {HardCeilings.MaxDailyLossFractionOfAllocation}].");

        return errors;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z]{3,5}$")]
    private static partial System.Text.RegularExpressions.Regex QuoteFormat();
}
