using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cix.Engine.Execution;

/// <summary>
/// Holdings (currency → total) at the last successful reconciliation, and the notional of the engine's open orders
/// then, which bounds how much fills since can move the holdings' value. Persisted so a deposit, withdrawal or outside
/// trade while the engine was down is still caught.
/// </summary>
public sealed record HoldingsBaseline(IReadOnlyDictionary<string, decimal> Holdings, decimal EngineOrdersNotional);

/// <summary>
/// Whether trading is halted, why, by whom and since when. <paramref name="AlsoBy"/>, <paramref name="AlsoReason"/> and
/// <paramref name="AlsoAt"/> record the latest other cause that would have halted trading while it already was (ADR-0018):
/// the dashboard's Resume releases only the halt the owner saw, so a cause that appears later must change what the page shows.
/// </summary>
public sealed record KillSwitchState(
    bool Engaged, string? Reason, string? By, DateTimeOffset? Since, string? AlsoBy = null, DateTimeOffset? AlsoAt = null, string? AlsoReason = null)
{
    public static readonly KillSwitchState Released = new(false, null, null, null);
}

/// <summary>
/// An order the engine sent and hasn't yet seen finish. Recorded before the order is sent, so after a crash or restart the
/// engine still recognises it as its own, can look up how it ended and journal its fill, and knows the plan behind it.
/// </summary>
/// <param name="ClientOrderId">The engine's ID for the order (a GUID: the exchange requires one, and it must be unique).</param>
/// <param name="Side"><c>buy</c> or <c>sell</c>.</param>
/// <param name="Source">What sent it: <c>place_order</c> (an agent), <c>protective-exit</c> (the engine's stop or target sell) or <c>engine</c>.</param>
/// <param name="VenueOrderId">The exchange's ID, once it confirmed the order; null while the outcome is unknown.</param>
public sealed record SentOrder(
    string ClientOrderId,
    string Symbol,
    string Side,
    decimal Size,
    decimal LimitPrice,
    DateTimeOffset SentAt,
    string Source,
    string? VenueOrderId = null,
    decimal? StopLoss = null,
    decimal? TakeProfit = null,
    string? RunId = null,
    string? Strategy = null,
    string? ExitReason = null);

/// <summary>
/// The plan for a position: the stop and target given with the buy that opened it. Written before that buy is sent, so a
/// restart at any point keeps it; it is removed once the position is gone.
/// </summary>
/// <param name="Status"><see cref="EntryPending"/> until the buy fills, then <see cref="Open"/>.</param>
/// <param name="EntryClientOrderId">The buy behind the plan (see <see cref="SentOrder"/> and the journal's <c>order</c> and <c>fill</c> entries).</param>
/// <param name="PendingClientOrderId">
/// A further buy for an open position, not filled yet. Its stop and target (<paramref name="PendingStopLoss"/>,
/// <paramref name="PendingTakeProfit"/>) replace the plan's only once it fills, so a buy that never fills can't move the stop.
/// </param>
public sealed record PositionPlanState(
    string Symbol,
    string Status,
    string EntryClientOrderId,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTimeOffset Since,
    decimal? EntryPrice = null,
    DateTimeOffset? FilledAt = null,
    string? Strategy = null,
    string? RunId = null,
    string? PendingClientOrderId = null,
    decimal? PendingStopLoss = null,
    decimal? PendingTakeProfit = null)
{
    public const string EntryPending = "entry-pending";
    public const string Open = "open";

    /// <summary>The <see cref="EntryClientOrderId"/> of a plan the protection made for a position bought without one.</summary>
    public const string AdoptedEntry = "adopted";
}

/// <summary>
/// The engine's operational state, which must survive a restart: the kill switch, the baselines for drawdown and daily
/// loss, the holdings at the last reconciliation, the orders it sent that may still be working, and the plan (stop and
/// target) for each position. Losing them would silently re-enable trading, reset the loss limits, make the engine's own
/// orders look like someone else's, or leave a position without its stop. The journal holds the history; this is "now".
/// </summary>
/// <param name="KnownClientOrderIds">Older format: bare client order IDs. Read for compatibility and folded into <see cref="Orders"/> as they finish.</param>
/// <param name="Orders">Orders sent and not yet seen finish, with what they are (see <see cref="SentOrder"/>).</param>
/// <param name="Positions">Position plans by symbol (see <see cref="PositionPlanState"/>).</param>
public sealed record EngineState(
    KillSwitchState KillSwitch,
    decimal PeakEquity,
    DateOnly? Day,
    decimal DayStartEquity,
    IReadOnlyList<string>? KnownClientOrderIds = null,
    HoldingsBaseline? Holdings = null,
    IReadOnlyList<SentOrder>? Orders = null,
    IReadOnlyDictionary<string, PositionPlanState>? Positions = null)
{
    /// <summary>Client order IDs the engine sent and hasn't yet seen finish; recorded before each send.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Known =>
        (Orders ?? []).Select(o => o.ClientOrderId).Concat(KnownClientOrderIds ?? []).Distinct(StringComparer.Ordinal).ToList();

    [JsonIgnore]
    public IReadOnlyList<SentOrder> SentOrders => Orders ?? [];

    [JsonIgnore]
    public IReadOnlyDictionary<string, PositionPlanState> Plans => Positions ?? new Dictionary<string, PositionPlanState>(StringComparer.Ordinal);

    /// <summary>
    /// Adds an order about to be sent. A buy with a stop or target also sets the plan for its symbol: a new plan, or, for a
    /// position still held, a pending stop and target that take over only if this buy fills. An open plan whose position is
    /// gone (sold since the last reconciliation, <paramref name="holdsPosition"/> false) never carries over to a new buy.
    /// </summary>
    public EngineState WithSent(SentOrder order, bool holdsPosition = true)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (Known.Contains(order.ClientOrderId, StringComparer.Ordinal))
            return this;

        var next = this with { Orders = [.. SentOrders, order] };
        if (order.Side != "buy")
            return next;

        var hasPlan = order.StopLoss is not null || order.TakeProfit is not null;
        var open = Plans.TryGetValue(order.Symbol, out var existing) && existing.Status == PositionPlanState.Open ? existing : null;
        if (open is not null && holdsPosition)
        {
            return hasPlan
                ? next with { Positions = WithPlan(order.Symbol, open with { PendingClientOrderId = order.ClientOrderId, PendingStopLoss = order.StopLoss, PendingTakeProfit = order.TakeProfit }) }
                : next; // a further buy without levels keeps the position's own
        }

        if (!hasPlan)
            return open is null ? next : next.WithoutPlans([order.Symbol]); // the old position's levels don't belong to this one
        var plan = new PositionPlanState(order.Symbol, PositionPlanState.EntryPending, order.ClientOrderId, order.StopLoss, order.TakeProfit, order.SentAt,
            Strategy: order.Strategy, RunId: order.RunId);
        return next with { Positions = next.WithPlan(order.Symbol, plan) };
    }

    /// <summary>Records the exchange's ID for a sent order.</summary>
    public EngineState WithVenueOrderId(string clientOrderId, string venueOrderId) =>
        SentOrders.Any(o => o.ClientOrderId == clientOrderId && o.VenueOrderId != venueOrderId)
            ? this with { Orders = SentOrders.Select(o => o.ClientOrderId == clientOrderId ? o with { VenueOrderId = venueOrderId } : o).ToList() }
            : this;

    /// <summary>Drops finished or refused orders (both formats).</summary>
    public EngineState WithoutOrders(IReadOnlyCollection<string> clientOrderIds)
    {
        ArgumentNullException.ThrowIfNull(clientOrderIds);
        if (!Known.Any(clientOrderIds.Contains))
            return this;
        var legacy = (KnownClientOrderIds ?? []).Where(id => !clientOrderIds.Contains(id)).ToList();
        return this with
        {
            Orders = SentOrders.Where(o => !clientOrderIds.Contains(o.ClientOrderId)).ToList(),
            KnownClientOrderIds = legacy.Count == 0 ? null : legacy,
        };
    }

    /// <summary>
    /// A buy filled: a plan waiting for it opens with the fill price; a further buy's pending stop and target take over.
    /// A replay after a restart changes nothing.
    /// </summary>
    public EngineState WithEntryFilled(string symbol, string clientOrderId, decimal averagePrice, DateTimeOffset at)
    {
        if (!Plans.TryGetValue(symbol, out var plan))
            return this;
        if (plan.PendingClientOrderId == clientOrderId)
        {
            return this with
            {
                Positions = WithPlan(symbol, plan with
                {
                    // A further buy that set only one level keeps the other: a stop is never dropped by adding to a position.
                    EntryClientOrderId = clientOrderId, StopLoss = plan.PendingStopLoss ?? plan.StopLoss, TakeProfit = plan.PendingTakeProfit ?? plan.TakeProfit,
                    EntryPrice = averagePrice, FilledAt = at, PendingClientOrderId = null, PendingStopLoss = null, PendingTakeProfit = null,
                }),
            };
        }

        if (plan.EntryClientOrderId != clientOrderId || (plan.Status == PositionPlanState.Open && plan.EntryPrice == averagePrice))
            return this;
        return this with { Positions = WithPlan(symbol, plan with { Status = PositionPlanState.Open, EntryPrice = averagePrice, FilledAt = at }) };
    }

    /// <summary>
    /// A buy ended unfilled (refused, cancelled, expired): a plan still waiting for it goes, and an open position drops the
    /// pending stop and target it would have brought, keeping its own.
    /// </summary>
    public EngineState WithEntryAbandoned(string clientOrderId)
    {
        if (Plans.Values.FirstOrDefault(p => p.PendingClientOrderId == clientOrderId) is { } open)
            return this with { Positions = WithPlan(open.Symbol, open with { PendingClientOrderId = null, PendingStopLoss = null, PendingTakeProfit = null }) };
        var symbol = Plans.Values.FirstOrDefault(p => p.EntryClientOrderId == clientOrderId && p.Status == PositionPlanState.EntryPending)?.Symbol;
        return symbol is null ? this : WithoutPlans([symbol]);
    }

    /// <summary>
    /// A stop for a held position that has none (a coin held before the engine traded it, the owner's own buy, an older buy
    /// without a stop), set once by the protection from the active strategy's rule (pre-live assessment F-02). A plan that has
    /// a stop keeps it; one without gets this stop and keeps its target; a position without a plan gets an open one, with the
    /// entry and target the journal had for it.
    /// </summary>
    public EngineState WithAdoptedStop(PositionPlan position, decimal stop, DateTimeOffset at, string? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stop);
        if (Plans.TryGetValue(position.Symbol, out var plan))
            return plan.StopLoss is > 0 ? this : this with { Positions = WithPlan(position.Symbol, plan with { StopLoss = stop }) };
        var adopted = new PositionPlanState(position.Symbol, PositionPlanState.Open, PositionPlanState.AdoptedEntry, stop, position.TakeProfit, at,
            EntryPrice: position.EntryPrice, FilledAt: position.FilledAt, Strategy: strategy);
        return this with { Positions = WithPlan(position.Symbol, adopted) };
    }

    public EngineState WithoutPlans(IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (!symbols.Any(Plans.ContainsKey))
            return this;
        var kept = Plans.Where(p => !symbols.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return this with { Positions = kept.Count == 0 ? null : kept };
    }

    private Dictionary<string, PositionPlanState> WithPlan(string symbol, PositionPlanState plan)
    {
        var plans = new Dictionary<string, PositionPlanState>(Plans, StringComparer.Ordinal) { [symbol] = plan };
        return plans;
    }

    public static readonly EngineState Initial = new(KillSwitchState.Released, 0m, null, 0m);
}

public interface IEngineStateStore
{
    EngineState Current { get; }

    /// <summary>Applies <paramref name="change"/> atomically and persists the result before returning it.</summary>
    EngineState Update(Func<EngineState, EngineState> change);
}

/// <summary>For paper trading, whose simulated balances are in memory too: both reset together on restart.</summary>
public sealed class InMemoryEngineStateStore : IEngineStateStore
{
    private readonly Lock _gate = new();

    public EngineState Current { get; private set; } = EngineState.Initial;

    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Current = change(Current);
            return Current;
        }
    }
}

/// <summary>
/// JSON file for live trading. Written to a temporary file, flushed to disk and moved into place, so a crash never
/// leaves half a file. Fails closed: an unreadable file stops startup, a missing file starts with the kill switch
/// engaged (a lost or moved file must not silently resume trading), and a failed write never keeps the switch released.
/// </summary>
public sealed class FileEngineStateStore : IEngineStateStore
{
    public const string NewFileReason = "No engine state file was found; the owner must confirm before trading starts.";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly string _path;

    public FileEngineStateStore(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The engine state file needs an absolute path.", nameof(path));

        _path = path;
        if (File.Exists(_path))
        {
            Current = Load(_path);
        }
        else
        {
            Current = EngineState.Initial with { KillSwitch = new KillSwitchState(true, NewFileReason, "engine", time.GetUtcNow()) };
            Write(Current);
        }
    }

    public EngineState Current { get; private set; }

    /// <summary>
    /// Persists, then publishes. Engaging the kill switch is the exception: it takes effect in memory first, so a disk
    /// that can't be written never leaves trading running (the write error still surfaces to the caller).
    /// </summary>
    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Current);
            if (next == Current)
                return Current;

            if (next.KillSwitch.Engaged && !Current.KillSwitch.Engaged)
                Current = next;

            Write(next);
            Current = next;
            return Current;
        }
    }

    private void Write(EngineState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static EngineState Load(string path)
    {

        try
        {
            var state = JsonSerializer.Deserialize<EngineState>(File.ReadAllText(path), Json);
            if (state?.KillSwitch is null)
                throw new InvalidOperationException($"The engine state file '{path}' has no kill switch state; fix or remove it deliberately.");
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The engine state file '{path}' is unreadable; fix or remove it deliberately.", ex);
        }
    }
}
