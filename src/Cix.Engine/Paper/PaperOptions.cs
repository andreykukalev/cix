namespace Cix.Engine.Paper;

/// <summary>Settings for the simulated account. Bound from the "Paper" configuration section.</summary>
public sealed class PaperOptions
{
    public const string SectionName = "Paper";

    /// <summary>Starting balances by currency, for example <c>{ "EUR": 200 }</c>.</summary>
    public Dictionary<string, decimal> InitialBalances { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Revolut X taker fee: 0.09%.</summary>
    public decimal TakerFeeRate { get; set; } = 0.0009m;

    /// <summary>Revolut X maker fee: 0%.</summary>
    public decimal MakerFeeRate { get; set; }

    /// <summary>Oldest quote accepted when placing an order.</summary>
    public TimeSpan MaxQuoteAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long pair rules (steps, limits, active flag) are cached before reloading.</summary>
    public TimeSpan PairsRefreshInterval { get; set; } = TimeSpan.FromHours(1);
}
