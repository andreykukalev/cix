namespace Cix.Domain.Risk;

/// <summary>
/// Absolute ceilings that configuration can tighten but never loosen.
/// OWNER-ONLY FILE: agents change it only on the owner's explicit request (see CLAUDE.md rule 3).
/// </summary>
public static class HardCeilings
{
    /// <summary>Largest allocation the bot may ever manage, in quote currency.</summary>
    public const decimal MaxAllocationQuote = 10_000m;

    /// <summary>Largest single order as a fraction of the allocation.</summary>
    public const decimal MaxOrderFractionOfAllocation = 0.25m;

    /// <summary>Drawdown from peak equity that always halts new risk.</summary>
    public const decimal MaxDrawdownFraction = 0.30m;

    /// <summary>Largest daily realised loss as a fraction of the allocation.</summary>
    public const decimal MaxDailyLossFractionOfAllocation = 0.10m;

    /// <summary>Most orders per minute, across all symbols.</summary>
    public const int MaxOrdersPerMinute = 10;

    /// <summary>Largest distance of a limit price from the reference price.</summary>
    public const decimal MaxPriceDeviationFraction = 0.05m;

    /// <summary>Oldest market data the gate accepts.</summary>
    public static readonly TimeSpan MaxMarketDataAge = TimeSpan.FromMinutes(5);
}
