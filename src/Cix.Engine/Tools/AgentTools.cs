using System.ComponentModel;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

/// <summary>
/// Read-only tools for trading agents. Each checks the active strategy spec allows it and journals the call.
/// Results contain market and portfolio data only: never keys, headers or raw exchange errors.
/// </summary>
[McpServerToolType]
public sealed class AgentTools(
    ToolCallContext context,
    FeatureService features,
    ExecutionService execution,
    KillSwitch killSwitch,
    RiskLimits limits,
    IJournal journal)
{
    /// <summary>How far back get_position_plans looks for the buy behind a position.</summary>
    public const int PlanLookbackDays = 30;

    [McpServerTool(Name = "get_market_view", ReadOnly = true)]
    [Description("Compact features for every tradable symbol: price, spread, trend, volatility, returns and 24h range. Ratios are fractions (0.05 = 5%). Check isStale before acting.")]
    public async Task<string> GetMarketView(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_market_view", cancellationToken).ConfigureAwait(false);
        var view = limits.AllowedSymbols.Order(StringComparer.Ordinal).Select(features.GetFeatures).ToList();
        await context.JournalAsync("get_market_view", $"{view.Count} symbols", null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(view);
    }

    [McpServerTool(Name = "get_features", ReadOnly = true)]
    [Description("Features for one symbol, for example BTC-EUR: price, bid, ask, spread, EMA 20/50 trend, ATR 14, 1h and 24h returns, 24h high/low and volume change.")]
    public async Task<string> GetFeatures(
        [Description("Symbol such as BTC-EUR.")] string symbol,
        CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_features", cancellationToken).ConfigureAwait(false);
        var normalised = ToolJson.Symbol(symbol, limits.AllowedSymbols);
        var result = features.GetFeatures(normalised);
        await context.JournalAsync("get_features", normalised, new Dictionary<string, string> { ["symbol"] = normalised }, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(result);
    }

    [McpServerTool(Name = "get_portfolio", ReadOnly = true)]
    [Description("Equity, peak, today's P&L, exposure, positions and open buys per symbol in quote currency, the risk limits that apply, and whether trading is halted.")]
    public async Task<string> GetPortfolio(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_portfolio", cancellationToken).ConfigureAwait(false);
        var valued = await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = valued?.Snapshot;
        var state = killSwitch.State;
        var result = new
        {
            available = snapshot is not null,
            asOf = valued?.At,
            snapshot?.Equity,
            snapshot?.PeakEquity,
            snapshot?.PnlToday,
            exposure = snapshot?.TotalExposureQuote,
            positions = snapshot?.PositionQuoteBySymbol,
            openBuys = snapshot?.OpenBuyQuoteBySymbol,
            halted = state.Engaged,
            haltReason = state.Engaged ? state.Reason : null,
            limits = new
            {
                limits.AllocationQuote,
                limits.MaxOrderQuote,
                limits.MaxPositionQuotePerSymbol,
                limits.MaxDailyLossQuote,
                limits.MaxDrawdownFraction,
                limits.MaxOrdersPerMinute,
                limits.MaxPriceDeviationFraction,
                allowedSymbols = limits.AllowedSymbols.Order(StringComparer.Ordinal),
            },
        };
        await context.JournalAsync("get_portfolio", snapshot is null ? "unavailable" : $"equity {snapshot.Equity:0.##}", null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(result);
    }

    [McpServerTool(Name = "get_open_orders", ReadOnly = true)]
    [Description("Open orders on the account: IDs, symbol, side, size, filled size, limit price and status.")]
    public async Task<string> GetOpenOrders(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_open_orders", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ExchangeOrder> open;
        try
        {
            open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException)
        {
            throw new McpException("Open orders could not be read from the exchange; try again shortly.");
        }

        var result = open.Where(o => o.IsOpen).Select(o => new
        {
            orderId = o.VenueOrderId,
            clientOrderId = ToolCallContext.IsSafeLabel(o.ClientOrderId) ? o.ClientOrderId : "foreign", // exchange text: never pass it raw to an agent
            symbol = MarketDataPoller.NormaliseSymbol(o.Symbol),
            o.Side,
            size = o.Quantity,
            filled = o.FilledQuantity,
            limitPrice = o.Price,
            o.Status,
            o.CreatedAt,
        }).ToList();
        await context.JournalAsync("get_open_orders", $"{result.Count} open", null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(result);
    }

    [McpServerTool(Name = "get_strategy_spec", ReadOnly = true)]
    [Description("The active strategy spec: its version label, instructions, parameters and the tools it allows. Read it at the start of every run. Always allowed, so an agent can learn its own rules.")]
    public async Task<string> GetStrategySpec(CancellationToken cancellationToken)
    {
        if (context.Spec is not { } spec)
            throw new McpException("No strategy spec is active.");
        await context.JournalAsync("get_strategy_spec", spec.Label, null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(new { version = spec.Label, spec.Status, spec.Instructions, spec.Parameters, spec.AllowedTools });
    }

    [McpServerTool(Name = "get_position_plans", ReadOnly = true)]
    [Description("For each symbol you hold enough of to sell: position value, sellableBaseSize (sell exactly this to exit), the fill price of the latest buy, and the stopLoss and takeProfit recorded with it. Check these at the start of every run: stops are not watched between runs.")]
    public async Task<string> GetPositionPlans(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_position_plans", cancellationToken).ConfigureAwait(false);
        var valued = await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false);
        if (valued is not { } portfolio)
            throw new McpException("The portfolio can't be valued right now; try again shortly.");

        // Positions worth selling only: dust below the exchange minimum can't be sold and isn't a position to manage.
        var sellable = portfolio.Snapshot.SellableBaseBySymbol ?? new Dictionary<string, decimal>();
        var held = sellable.Keys.ToHashSet(StringComparer.Ordinal);
        var from = DateOnly.FromDateTime(portfolio.At.UtcDateTime).AddDays(-PlanLookbackDays);
        var fills = held.Count == 0 ? [] : await journal.QueryAsync(new JournalQuery(From: from, Kind: "fill", Limit: FileJournal.MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        var orders = held.Count == 0 ? [] : await journal.QueryAsync(new JournalQuery(From: from, Kind: "order", Limit: FileJournal.MaxQueryLimit), cancellationToken).ConfigureAwait(false);

        var plans = held.Order(StringComparer.Ordinal).Select(symbol =>
        {
            // Newest first: the latest buy fill for this symbol is the position's entry.
            var fill = fills.FirstOrDefault(f => f.Data?.GetValueOrDefault("symbol") == symbol && f.Data?.GetValueOrDefault("side") == "buy");
            var clientOrderId = fill?.Data?.GetValueOrDefault("clientOrderId");
            var order = clientOrderId is null ? null : orders.FirstOrDefault(o => o.Data?.GetValueOrDefault("clientOrderId") == clientOrderId && o.Data?.GetValueOrDefault("tool") == "place_order");
            return new
            {
                symbol,
                positionQuote = portfolio.Snapshot.PositionQuoteBySymbol[symbol],
                sellableBaseSize = sellable[symbol],
                entryPrice = Number(fill?.Data?.GetValueOrDefault("averagePrice")),
                filledAt = fill?.At,
                stopLoss = Number(order?.Data?.GetValueOrDefault("stopLoss")),
                takeProfit = Number(order?.Data?.GetValueOrDefault("takeProfit")),
                note = fill is null ? "No completed buy in the journal for this position (bought earlier, outside the engine, or a buy still partly filled): no stop or target recorded." : null,
            };
        }).ToList();

        await context.JournalAsync("get_position_plans", $"{plans.Count} positions", null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(plans);
    }

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
