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

    /// <summary>Largest change in holdings value, at current prices, that fills and fees can explain between two reconciliations.</summary>
    public decimal BalanceToleranceQuote { get; set; } = 2m;

    public void Validate()
    {
        if (ReconcileInterval <= TimeSpan.Zero || StaleDataKillAfter <= TimeSpan.Zero || UncertainTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Safety intervals must be positive.");
        if (MaxConsecutiveRejects < 1)
            throw new InvalidOperationException("Safety:MaxConsecutiveRejects must be at least 1.");
        if (BalanceToleranceQuote <= 0)
            throw new InvalidOperationException("Safety:BalanceToleranceQuote must be positive.");
    }
}
