using System.Globalization;
using Cix.Engine.Journal;

namespace Cix.Engine.Execution;

/// <summary>A held position with the stop and target recorded when it was bought (null when the journal has no buy for it).</summary>
public sealed record PositionPlan(
    string Symbol,
    decimal PositionQuote,
    decimal SellableBaseSize,
    decimal? EntryPrice,
    DateTimeOffset? FilledAt,
    decimal? StopLoss,
    decimal? TakeProfit)
{
    public bool HasEntry => FilledAt is not null;
}

/// <summary>
/// Joins sellable positions with their plans: from the engine state (written before the buy was sent, so it survives any
/// restart), or, for a position the state has no plan for (opened before plans were kept there), the latest buy fill in the
/// journal and the order behind it.
/// </summary>
public static class PositionPlans
{
    /// <summary>How far back to look in the journal for the buy behind a position without a plan in the state.</summary>
    public const int LookbackDays = 30;

    /// <summary>Plans for every position big enough to sell, or null if the portfolio can't be valued now.</summary>
    public static async Task<IReadOnlyList<PositionPlan>?> LoadAsync(ExecutionService execution, IJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(journal);
        var valued = await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false);
        if (valued is not { } portfolio)
            return null;

        // Positions worth selling only: dust below the exchange minimum can't be sold and isn't a position to manage.
        var sellable = portfolio.Snapshot.SellableBaseBySymbol ?? new Dictionary<string, decimal>();
        if (sellable.Count == 0)
            return [];

        var states = execution.PositionPlanStates;
        IReadOnlyList<JournalEntry> fills = [];
        IReadOnlyList<JournalEntry> orders = [];
        if (sellable.Keys.Any(symbol => !states.ContainsKey(symbol)))
        {
            var from = DateOnly.FromDateTime(portfolio.At.UtcDateTime).AddDays(-LookbackDays);
            fills = await journal.QueryAsync(new JournalQuery(From: from, Kind: "fill", Limit: FileJournal.MaxQueryLimit), cancellationToken).ConfigureAwait(false);
            orders = await journal.QueryAsync(new JournalQuery(From: from, Kind: "order", Limit: FileJournal.MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        }

        return sellable.Keys.Order(StringComparer.Ordinal).Select(symbol =>
        {
            if (states.TryGetValue(symbol, out var state))
            {
                return new PositionPlan(
                    symbol, portfolio.Snapshot.PositionQuoteBySymbol[symbol], sellable[symbol],
                    state.EntryPrice, state.FilledAt ?? state.Since, state.StopLoss, state.TakeProfit);
            }

            // Newest first: the latest buy fill for this symbol is the position's entry.
            var fill = fills.FirstOrDefault(f => f.Data?.GetValueOrDefault("symbol") == symbol && f.Data?.GetValueOrDefault("side") == "buy");
            var clientOrderId = fill?.Data?.GetValueOrDefault("clientOrderId");
            var order = clientOrderId is null ? null : orders.FirstOrDefault(o => o.Data?.GetValueOrDefault("clientOrderId") == clientOrderId && o.Data?.GetValueOrDefault("tool") == "place_order");
            return new PositionPlan(
                symbol,
                portfolio.Snapshot.PositionQuoteBySymbol[symbol],
                sellable[symbol],
                Number(fill?.Data?.GetValueOrDefault("averagePrice")),
                fill?.At,
                Number(order?.Data?.GetValueOrDefault("stopLoss") ?? fill?.Data?.GetValueOrDefault("stopLoss")),
                Number(order?.Data?.GetValueOrDefault("takeProfit") ?? fill?.Data?.GetValueOrDefault("takeProfit")));
        }).ToList();
    }

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}
