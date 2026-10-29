using System.Text.RegularExpressions;

namespace Cix.Stocks;

public enum InstrumentKind
{
    Stock,
    Etf,
}

/// <summary>A stock or ETF the advisor may recommend.</summary>
/// <param name="Symbol">Ticker and exchange as the data provider names them, for example <c>AAPL.US</c>, <c>SAP.XETRA</c> or <c>CSPX.LSE</c>.</param>
/// <param name="Currency">The currency the instrument trades in, for example <c>USD</c>.</param>
/// <param name="QuantityStep">The smallest quantity the owner can buy: 1 for whole shares, smaller where the broker sells fractions.</param>
/// <param name="PriceTick">Recommended prices (stops) are rounded to this, down for stops so they never sit above the computed level.</param>
public sealed record Instrument(
    string Symbol,
    string Name,
    string Currency,
    InstrumentKind Kind,
    decimal QuantityStep = 1m,
    decimal PriceTick = 0.01m);

public static partial class StockSymbol
{
    /// <summary>
    /// <c>TICKER.EXCHANGE</c> in capitals, digits, <c>-</c> and <c>_</c>. Symbols end up in request paths, so anything else
    /// (slashes, dots in the ticker, query characters) is refused before a request is built.
    /// </summary>
    public static bool IsValid(string? symbol) => symbol is { Length: <= 32 } && Pattern().IsMatch(symbol);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_-]{0,19}\.[A-Z]{1,10}\z")]
    private static partial Regex Pattern();
}

/// <summary>A position the owner holds at the broker, as they recorded it. The advisor can't read the broker account.</summary>
/// <param name="Stop">The stop order the owner has at the broker, or null if there is none.</param>
/// <param name="OpenedOn">The trading day the position was bought; the trailing stop follows the highest close since then.</param>
public sealed record Holding(string Symbol, decimal Quantity, decimal AverageCost, decimal? Stop, DateOnly OpenedOn);
