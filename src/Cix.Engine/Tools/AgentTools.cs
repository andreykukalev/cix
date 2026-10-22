using System.ComponentModel;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
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
    IExchangeClient exchange,
    KillSwitch killSwitch,
    RiskLimits limits)
{
    [McpServerTool(Name = "get_market_view", ReadOnly = true)]
    [Description("Compact features for every tradable symbol: price, spread, trend, volatility, returns and 24h range. Ratios are fractions (0.05 = 5%). Check isStale before acting.")]
    public async Task<string> GetMarketView(CancellationToken cancellationToken)
    {
        context.RequireAllowed("get_market_view");
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
        context.RequireAllowed("get_features");
        var normalised = ToolJson.Symbol(symbol, limits.AllowedSymbols);
        var result = features.GetFeatures(normalised);
        await context.JournalAsync("get_features", normalised, new Dictionary<string, string> { ["symbol"] = normalised }, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(result);
    }

    [McpServerTool(Name = "get_portfolio", ReadOnly = true)]
    [Description("Equity, peak, today's P&L, exposure, positions and open buys per symbol in quote currency, the risk limits that apply, and whether trading is halted.")]
    public async Task<string> GetPortfolio(CancellationToken cancellationToken)
    {
        context.RequireAllowed("get_portfolio");
        var snapshot = await execution.SampleAsync(cancellationToken).ConfigureAwait(false);
        var state = killSwitch.State;
        var result = new
        {
            available = snapshot is not null,
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
        context.RequireAllowed("get_open_orders");
        IReadOnlyList<ExchangeOrder> open;
        try
        {
            open = await exchange.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException)
        {
            throw new McpException("Open orders could not be read from the exchange; try again shortly.");
        }

        var result = open.Where(o => o.IsOpen).Select(o => new
        {
            orderId = o.VenueOrderId,
            o.ClientOrderId,
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
    [Description("The active strategy spec: its version label, instructions, parameters and the tools it allows. Read it at the start of every run.")]
    public async Task<string> GetStrategySpec(CancellationToken cancellationToken)
    {
        if (context.Spec is not { } spec)
            throw new McpException("No strategy spec is active.");
        await context.JournalAsync("get_strategy_spec", spec.Label, null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(new { version = spec.Label, spec.Status, spec.Instructions, spec.Parameters, spec.AllowedTools });
    }
}
