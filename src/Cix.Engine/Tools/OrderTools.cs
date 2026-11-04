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
public sealed class OrderTools(
    ToolCallContext context,
    ExecutionService execution,
    RiskLimits limits,
    Analysis.MarketViewStore? views = null,
    TimeProvider? time = null,
    TradableSymbols? tradable = null)
{
    public const decimal MinValue = 0.00000001m;
    public const decimal MaxBaseSize = 1_000_000m;
    public const decimal MaxPrice = 1_000_000_000m;
    public const int MinRationale = 10;
    public const int MaxRationale = 1_000;

    /// <summary>Least distance between a buy's limit price and its stop or target.</summary>
    public const decimal MinExitDistance = Cix.Domain.Scanning.MarketScanner.MinExitDistanceFraction;

    [McpServerTool(Name = "place_order", Destructive = false, Idempotent = false)]
    [Description("Places a limit order. The engine rounds it to the exchange's steps and the risk gate may accept, resize or reject it; the result says which and why. Explain the decision in rationale: it goes into the journal the owner reviews.")]
    public async Task<string> PlaceOrder(
        [Description("Symbol such as BTC-EUR.")] string symbol,
        [Description("buy or sell.")] string side,
        [Description("Size in the base currency, for example 0.0005 BTC.")] decimal baseSize,
        [Description("Limit price in the quote currency.")] decimal limitPrice,
        [Description("Why this order, in 10 to 1000 characters.")] string rationale,
        [Description("true to only add liquidity: if it would fill at once it is rejected instead.")] bool postOnly = false,
        [Description("Required for buys, not allowed for sells: the stop-loss price, at least 0.5% below the limit price. Recorded and returned by get_position_plans; the engine sells the position itself when the bid reaches it.")] decimal? stopLoss = null,
        [Description("Buys only: the take-profit price, at least 0.5% above the limit price. The engine sells at it like at the stop.")] decimal? takeProfit = null,
        [Description(RationaleRuHelp)] string? rationaleRu = null,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("place_order", cancellationToken).ConfigureAwait(false);
        var normalised = ToolJson.Symbol(symbol, tradable?.Current ?? TradableSymbols.None);
        var orderSide = side?.Trim().ToLowerInvariant() switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => throw new McpException("side must be buy or sell."),
        };
        if (baseSize is < MinValue or > MaxBaseSize || limitPrice is < MinValue or > MaxPrice)
            throw new McpException($"baseSize must be between {MinValue} and {MaxBaseSize}, and limitPrice between {MinValue} and {MaxPrice}.");
        var why = Rationale(rationale);
        var whyRu = RationaleRu(rationaleRu);
        if (orderSide == OrderSide.Sell && (stopLoss is not null || takeProfit is not null))
            throw new McpException("stopLoss and takeProfit apply to buys only.");
        // Every agent buy carries a stop the engine sells at by itself (pre-live assessment F-02): no position is left unwatched.
        if (orderSide == OrderSide.Buy && stopLoss is null)
            throw new McpException("A buy needs a stopLoss: use the signal's, or stopAtr × the hourly ATR below the price, as the scanner does.");
        // The engine sells at these levels by itself (protective exits), so they must be a real distance from the entry.
        if (stopLoss is { } sl && (sl <= 0 || sl > limitPrice * (1 - MinExitDistance)))
            throw new McpException($"stopLoss must be positive and at least {MinExitDistance:P1} below limitPrice.");
        if (takeProfit is { } tp && (tp < limitPrice * (1 + MinExitDistance) || tp > MaxPrice))
            throw new McpException($"takeProfit must be at least {MinExitDistance:P1} above limitPrice.");

        // The market view (ADR-0007) holds new buys, not only scanner signals. Sells always pass. Fixed wording: the
        // analyst's own text never reaches the trader.
        var now = (time ?? TimeProvider.System).GetUtcNow();
        if (orderSide == OrderSide.Buy && views?.Current is { } view)
        {
            if (view.BlocksEntries(now))
                throw new McpException($"New buys are paused by the market view (risk-off) until {view.ValidUntil:yyyy-MM-dd HH:mm} UTC. Sells are allowed.");
            if (view.FlagFor(normalised, now) is { } flag)
                throw new McpException($"{normalised} is on the market view's avoid list until {flag.Until:yyyy-MM-dd HH:mm} UTC. Sells are allowed.");
        }

        // The engine names the order, so an agent can't collide with or impersonate another order's ID.
        var clientOrderId = Guid.NewGuid().ToString();
        // Read once: the spec can switch between runs (#105), and this order's label and limits must come from the same one.
        var spec = context.Spec;
        var result = await execution.PlaceAsync(
            new OrderIntent(normalised, orderSide, baseSize, limitPrice, clientOrderId), postOnly, cancellationToken,
            context: new OrderContext("place_order", stopLoss, takeProfit, context.RunId, spec?.Label,
                ExitReason: orderSide == OrderSide.Sell ? ExitReasons.Agent : null),
            strategy: Strategy.StrategySchema.LimitsOf(spec, limits)).ConfigureAwait(false);

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
        if (whyRu is not null)
            data["rationaleRu"] = whyRu; // for the owner's dashboard only
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
        [Description(RationaleRuHelp)] string? rationaleRu = null,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("cancel_order", cancellationToken).ConfigureAwait(false);
        if (!ToolCallContext.IsSafeLabel(orderId))
            throw new McpException("orderId is not valid.");
        var why = Rationale(rationale);
        var whyRu = RationaleRu(rationaleRu);

        var (outcome, clientOrderId) = await execution.CancelOwnOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, string>
        {
            ["tool"] = "cancel_order",
            ["orderId"] = orderId,
            ["clientOrderId"] = clientOrderId ?? string.Empty,
            ["status"] = outcome.ToString().ToLowerInvariant(),
            ["rationale"] = why,
        };
        if (whyRu is not null)
            data["rationaleRu"] = whyRu;
        await context.RecordOutcomeAsync("order", $"Cancel {orderId}: {outcome}", data).ConfigureAwait(false);

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

    /// <summary>What the owner's dashboard asks of the Russian rationale, shared by every tool that takes one.</summary>
    internal const string RationaleRuHelp =
        "rationale in Russian for the owner, whose native language it is: the same meaning in plain, natural Russian, 10 to 1000 "
        + "characters, adding nothing the English doesn't say. Keep coin names and tickers (BTC-EUR), abbreviations (ATR, EMA20) and "
        + "trading terms (stop-loss, take-profit) in English. Shown on the dashboard instead of the English; leave it out only if you can't write it.";

    /// <summary>Required, trimmed, single-line and bounded: it is written to the journal verbatim.</summary>
    internal static string Rationale(string? rationale)
    {
        var text = Clean(rationale);
        if (text.Length < MinRationale)
            throw new McpException($"rationale is required ({MinRationale} to {MaxRationale} characters): say why.");
        return Bounded(text);
    }

    /// <summary>The optional Russian rationale, cleaned like the English one; null when absent or too short to say anything.</summary>
    internal static string? RationaleRu(string? rationaleRu)
    {
        var text = Clean(rationaleRu);
        return text.Length < MinRationale ? null : Bounded(text); // optional: never fails the order or the answer
    }

    private static string Clean(string? text)
    {
        var single = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Drop control and format characters (zero-width, bidi overrides) that could disguise what the owner reads.
        return new string(single.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
    }

    private static string Bounded(string text)
    {
        if (text.Length <= MaxRationale)
            return text;
        var cut = char.IsHighSurrogate(text[MaxRationale - 1]) ? MaxRationale - 1 : MaxRationale; // never split a character
        return text[..cut];
    }
}
