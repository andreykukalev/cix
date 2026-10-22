namespace Cix.Engine;

public enum TradingMode
{
    Paper,
    Live,
}

/// <summary>
/// Reads <c>Trading:Mode</c>. Missing means Paper; anything other than Paper or Live stops startup.
/// Live is also refused outside the Production environment, so a developer or agent machine can't trade real money
/// even if the setting is changed there.
/// </summary>
public static class TradingModeResolver
{
    public const string Key = "Trading:Mode";
    public const string ProductionEnvironment = "Production";

    public static TradingMode Resolve(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[Key];

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "Paper", StringComparison.OrdinalIgnoreCase))
            return TradingMode.Paper;

        // The mode that spends real money must be spelled exactly.
        if (!string.Equals(value.Trim(), nameof(TradingMode.Live), StringComparison.Ordinal))
            throw new InvalidOperationException($"{Key} must be 'Paper' or 'Live'.");

        if (!string.Equals(environmentName, ProductionEnvironment, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Key} is Live but the environment is '{environmentName}'; Live runs only in {ProductionEnvironment}.");

        return TradingMode.Live;
    }
}

/// <summary>The resolved mode, registered in DI (an enum can't be registered directly).</summary>
public sealed record TradingContext(TradingMode Mode);
