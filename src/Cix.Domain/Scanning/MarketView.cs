namespace Cix.Domain.Scanning;

public enum MarketRegime
{
    Neutral,
    RiskOn,
    RiskOff,
}

/// <summary>A symbol the analyst wants left alone (for example after a hack or before a token unlock) until a time.</summary>
public sealed record SymbolFlag(string Symbol, DateTimeOffset Until);

/// <summary>
/// The market analyst's latest view (#25). The scanner applies only its structure: risk-off blocks new entries and a flag
/// excludes a symbol from entries. Exits are never blocked, and an expired view counts as no view, so the analyst can only
/// make trading more cautious, never riskier, and never stop it for longer than the view lasts. The analyst's own words stay in
/// the journal for the owner; they never reach the trader, because they come from the web.
/// </summary>
public sealed record MarketView(MarketRegime Regime, DateTimeOffset RecordedAt, DateTimeOffset ValidUntil, IReadOnlyList<SymbolFlag> Flags)
{
    public bool IsActive(DateTimeOffset now) => now >= RecordedAt && now < ValidUntil;

    /// <summary>Whether <paramref name="symbol"/> is flagged at <paramref name="now"/> (only while the view itself is active).</summary>
    public SymbolFlag? FlagFor(string symbol, DateTimeOffset now) =>
        IsActive(now) ? Flags.FirstOrDefault(f => string.Equals(f.Symbol, symbol, StringComparison.Ordinal) && now < f.Until) : null;

    public bool BlocksEntries(DateTimeOffset now) => IsActive(now) && Regime == MarketRegime.RiskOff;
}
