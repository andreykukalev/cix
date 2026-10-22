namespace Cix.Engine.Execution;

/// <summary>Automatic kill-switch triggers and reconciliation. Bound from the "Safety" section.</summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>How often open orders and balances are checked against what the engine expects.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>No fresh quote for an allowed symbol for this long engages the kill switch (the gate already refuses orders after 5 minutes).</summary>
    public TimeSpan StaleDataKillAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>This many risk-gate rejections in a row engages the kill switch: something is asking for orders it shouldn't.</summary>
    public int MaxConsecutiveRejects { get; set; } = 10;

    /// <summary>An order with an unknown outcome that still isn't listed after this long is taken as never placed (or finished).</summary>
    public TimeSpan UncertainTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Change in holdings value, at current prices, allowed between reconciliations regardless of fills.</summary>
    public decimal BalanceToleranceQuote { get; set; } = 2m;

    /// <summary>
    /// Extra allowance per unit of the engine's order notional: a fill at the limit price (up to the gate's price band
    /// away), the fee, and the price moving on the new position before the next reconciliation.
    /// </summary>
    public const decimal FillAllowanceFraction = 0.03m;

    /// <summary>Checks the options can't quietly disable a trigger. With <paramref name="limits"/>, also checks against the allocation.</summary>
    public void Validate(Domain.Risk.RiskLimits? limits = null)
    {
        if (ReconcileInterval <= TimeSpan.Zero || ReconcileInterval > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Safety:ReconcileInterval must be between 0 and 15 minutes.");
        if (StaleDataKillAfter < TimeSpan.FromMinutes(5) || StaleDataKillAfter > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Safety:StaleDataKillAfter must be between 5 and 60 minutes.");
        if (UncertainTimeout < ReconcileInterval || UncertainTimeout > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Safety:UncertainTimeout must be at least ReconcileInterval and at most 1 hour.");
        if (MaxConsecutiveRejects is < 1 or > 100)
            throw new InvalidOperationException("Safety:MaxConsecutiveRejects must be between 1 and 100.");
        if (BalanceToleranceQuote <= 0)
            throw new InvalidOperationException("Safety:BalanceToleranceQuote must be positive.");
        if (limits is not null && BalanceToleranceQuote > limits.AllocationQuote * 0.05m)
            throw new InvalidOperationException("Safety:BalanceToleranceQuote must be at most 5% of the allocation.");
    }
}
