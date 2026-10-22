namespace Cix.Engine;

public enum TradingMode
{
    Paper,
    Live,
}

/// <summary>Reads <c>Trading:Mode</c>. Missing means Paper; anything other than Paper or Live stops startup.</summary>
public static class TradingModeResolver
{
    public const string Key = "Trading:Mode";

    public static TradingMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[Key];

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "Paper", StringComparison.OrdinalIgnoreCase))
            return TradingMode.Paper;

        // Live must be spelled exactly: no case-insensitive or numeric matches for the mode that spends real money.
        if (string.Equals(value.Trim(), "Live", StringComparison.Ordinal))
            return TradingMode.Live;

        throw new InvalidOperationException($"{Key} must be 'Paper' or 'Live'.");
    }
}

/// <summary>The resolved mode, registered in DI (an enum can't be registered directly).</summary>
public sealed record TradingContext(TradingMode Mode);
