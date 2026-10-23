using System.ComponentModel;
using System.Globalization;
using Cix.Domain;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

/// <summary>
/// Order tools for trading agents. Orders go through <see cref="ExecutionService"/>, so every one meets the risk gate;
/// the tool only validates arguments, assigns the client order ID and records the agent's rationale in the journal.
/// </summary>
[McpServerToolType]
public sealed class OrderTools(ToolCallContext context, ExecutionService execution, RiskLimits limits)
{
    public const decimal MinValue = 0.00000001m;
    public const decimal MaxBaseSize = 1_000_000m;
    public const decimal MaxPrice = 1_000_000_000m;
    public const int MinRationale = 10;
    public const int MaxRationale = 1_000;

    [McpServerTool(Name = "place_order", Destructive = false, Idempotent = false)]
    [Description("Places a limit order. The engine rounds it to the exchange's steps and the risk gate may accept, resize or reject it; the result says which and why. Explain the decision in rationale: it goes into the journal the owner reviews.")]
    public async Task<string> PlaceOrder(
        [Description("Symbol such as BTC-EUR.")] string symbol,
        [Description("buy or sell.")] string side,
        [Description("Size in the base currency, for example 0.0005 BTC.")] decimal baseSize,
        [Description("Limit price in the quote currency.")] decimal limitPrice,
        [Description("Why this order, in 10 to 1000 characters.")] string rationale,
        [Description("true to only add liquidity: if it would fill at once it is rejected instead.")] bool postOnly = false,
        [Description("Buys only: the stop-loss price you plan to exit at, below the limit price. Recorded and returned by get_position_plans; the engine doesn't place it.")] decimal? stopLoss = null,
        [Description("Buys only: the take-profit price, above the limit price. Recorded like stopLoss.")] decimal? takeProfit = null,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("place_order", cancellationToken).ConfigureAwait(false);
        var normalised = ToolJson.Symbol(symbol, limits.AllowedSymbols);
        var orderSide = side?.Trim().ToLowerInvariant() switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => throw new McpException("side must be buy or sell."),
        };
        if (baseSize is < MinValue or > MaxBaseSize || limitPrice is < MinValue or > MaxPrice)
            throw new McpException($"baseSize must be between {MinValue} and {MaxBaseSize}, and limitPrice between {MinValue} and {MaxPrice}.");
        var why = Rationale(rationale);
        if (orderSide == OrderSide.Sell && (stopLoss is not null || takeProfit is not null))
            throw new McpException("stopLoss and takeProfit apply to buys only.");
        if (stopLoss is { } sl && (sl <= 0 || sl >= limitPrice))
            throw new McpException("stopLoss must be positive and below limitPrice.");
        if (takeProfit is { } tp && (tp <= limitPrice || tp > MaxPrice))
            throw new McpException("takeProfit must be above limitPrice.");

        // The engine names the order, so an agent can't collide with or impersonate another order's ID.
        var clientOrderId = Guid.NewGuid().ToString();
        var result = await execution.PlaceAsync(
            new OrderIntent(normalised, orderSide, baseSize, limitPrice, clientOrderId), postOnly, cancellationToken).ConfigureAwait(false);

        var inv = CultureInfo.InvariantCulture;
        var data = new Dictionary<string, string>
        {
            ["tool"] = "place_order",
            ["clientOrderId"] = clientOrderId,
            ["symbol"] = normalised,
            ["side"] = orderSide.ToString().ToLowerInvariant(),
            ["requestedSize"] = baseSize.ToString(inv),
            ["requestedPrice"] = limitPrice.ToString(inv),
            ["postOnly"] = postOnly ? "true" : "false",
            ["status"] = result.Status.ToString().ToLowerInvariant(),
            ["rule"] = result.Rule,
            ["reason"] = result.Reason,
            ["rationale"] = why,
        };
        if (stopLoss is { } stop)
            data["stopLoss"] = stop.ToString(inv);
        if (takeProfit is { } target)
            data["takeProfit"] = target.ToString(inv);
        if (result.Order is { } approved)
        {
            data["approvedSize"] = approved.BaseSize.ToString(inv);
            data["approvedPrice"] = approved.LimitPrice.ToString(inv);
        }

        if (result.Placed is { } placed)
            data["orderId"] = placed.VenueOrderId;

        await context.RecordOutcomeAsync("order", $"{orderSide} {baseSize.ToString(inv)} {normalised} @ {limitPrice.ToString(inv)}: {result.Status} ({result.Rule})", data).ConfigureAwait(false);

        return ToolJson.Of(new
        {
            result.Status,
            result.Rule,
            result.Reason,
            clientOrderId,
            orderId = result.Placed?.VenueOrderId,
            approved = result.Order is { } o ? new { size = o.BaseSize, price = o.LimitPrice } : null,
        });
    }

    [McpServerTool(Name = "cancel_order", Destructive = true, Idempotent = true)]
    [Description("Cancels one of the engine's open orders by its orderId (from place_order or get_open_orders). Explain why in rationale.")]
    public async Task<string> CancelOrder(
        [Description("The orderId to cancel.")] string orderId,
        [Description("Why, in 10 to 1000 characters.")] string rationale,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("cancel_order", cancellationToken).ConfigureAwait(false);
        if (!ToolCallContext.IsSafeLabel(orderId))
            throw new McpException("orderId is not valid.");
        var why = Rationale(rationale);

        var (outcome, clientOrderId) = await execution.CancelOwnOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        await context.RecordOutcomeAsync(
            "order",
            $"Cancel {orderId}: {outcome}",
            new Dictionary<string, string>
            {
                ["tool"] = "cancel_order",
                ["orderId"] = orderId,
                ["clientOrderId"] = clientOrderId ?? string.Empty,
                ["status"] = outcome.ToString().ToLowerInvariant(),
                ["rationale"] = why,
            }).ConfigureAwait(false);

        return ToolJson.Of(new
        {
            status = outcome,
            message = outcome switch
            {
                CancelOutcome.Cancelled => "Cancellation requested.",
                CancelOutcome.NotFound => "No open order with that ID; it may have filled or been cancelled already.",
                CancelOutcome.NotOwn => "That order wasn't placed by this engine, so it isn't cancelled here.",
                _ => "The exchange could not be reached; try again shortly.",
            },
        });
    }

    /// <summary>Required, trimmed, single-line and bounded: it is written to the journal verbatim.</summary>
    private static string Rationale(string? rationale)
    {
        var text = string.Join(' ', (rationale ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Drop control and format characters (zero-width, bidi overrides) that could disguise what the owner reads.
        text = new string(text.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        if (text.Length < MinRationale)
            throw new McpException($"rationale is required ({MinRationale} to {MaxRationale} characters): say why.");
        if (text.Length <= MaxRationale)
            return text;
        var cut = char.IsHighSurrogate(text[MaxRationale - 1]) ? MaxRationale - 1 : MaxRationale; // never split a character
        return text[..cut];
    }
}
