using Cix.Domain.Exchange;

namespace Cix.Domain.Risk;

/// <summary>A coin held, as a strategy switch sees it: its value, how much of it can be sold, the bid and the pair's rules.</summary>
public sealed record TrimHolding(string Symbol, decimal ValueQuote, decimal SellableBaseSize, decimal Bid, PairInfo Pair);

/// <summary>A sell that brings a holding back within the strategy's caps, at the bid.</summary>
public sealed record TrimSell(string Symbol, decimal BaseSize, decimal LimitPrice, string Rule, string Reason);

/// <summary>
/// After a switch to a strategy with smaller caps: the sells that bring holdings within its per-coin
/// cap and then its total exposure cap, largest holdings first. Only what is above a cap is sold, rounded up to the
/// exchange's step; a remainder too small to sell later goes too. Holdings within the caps are never touched. The sells still
/// go through the risk gate. Deterministic and side-effect free.
/// </summary>
public static class StrategyTrim
{
    public static IReadOnlyList<TrimSell> Plan(IReadOnlyList<TrimHolding> holdings, StrategyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxPositionQuotePerSymbol is null && limits.MaxExposureQuote is null)
            return [];

        var usable = holdings.Where(h => h.Bid > 0 && h.ValueQuote > 0 && h.SellableBaseSize > 0).ToList();
        var excess = usable.ToDictionary(h => h.Symbol, _ => 0m, StringComparer.Ordinal);
        var rule = usable.ToDictionary(h => h.Symbol, _ => "", StringComparer.Ordinal);

        if (limits.MaxPositionQuotePerSymbol is { } perCoin)
        {
            foreach (var h in usable.Where(h => h.ValueQuote > perCoin))
            {
                excess[h.Symbol] = h.ValueQuote - perCoin;
                rule[h.Symbol] = "strategy-position-cap";
            }
        }

        if (limits.MaxExposureQuote is { } cap)
        {
            var over = usable.Sum(h => h.ValueQuote - excess[h.Symbol]) - cap;
            foreach (var h in usable.OrderByDescending(h => h.ValueQuote - excess[h.Symbol]).ThenBy(h => h.Symbol, StringComparer.Ordinal))
            {
                if (over <= 0)
                    break;
                var take = Math.Min(over, h.ValueQuote - excess[h.Symbol]);
                excess[h.Symbol] += take;
                over -= take;
                if (rule[h.Symbol].Length == 0)
                    rule[h.Symbol] = "strategy-exposure-cap";
            }
        }

        var sells = new List<TrimSell>();
        foreach (var h in usable.Where(h => excess[h.Symbol] > 0).OrderBy(h => h.Symbol, StringComparer.Ordinal))
        {
            var step = h.Pair.BaseStep > 0 ? h.Pair.BaseStep : 0.00000001m;
            var size = Math.Min(Steps.Ceiling(excess[h.Symbol] / h.Bid, step), h.SellableBaseSize);
            if (h.SellableBaseSize - size < h.Pair.MinOrderSize || (h.SellableBaseSize - size) * h.Bid < h.Pair.MinOrderSizeQuote)
                size = h.SellableBaseSize; // what would be left couldn't be sold on its own
            if (size < h.Pair.MinOrderSize || size * h.Bid < h.Pair.MinOrderSizeQuote)
                continue; // too small for the exchange: within a step of the cap, leave it
            var what = rule[h.Symbol] == "strategy-position-cap"
                ? $"the strategy's cap of {limits.MaxPositionQuotePerSymbol:0.##} per coin"
                : $"the strategy's total exposure cap of {limits.MaxExposureQuote:0.##}";
            sells.Add(new TrimSell(h.Symbol, size, h.Bid, rule[h.Symbol],
                $"Holding worth {h.ValueQuote:0.##} trimmed by about {size * h.Bid:0.##} to stay within {what}."));
        }

        return sells;
    }
}
