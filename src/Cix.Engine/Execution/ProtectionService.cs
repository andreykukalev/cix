using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>The engine's own stop-loss exits, independent of the strategy and the scan. Bound from the "Protection" section.</summary>
public sealed class ProtectionOptions
{
    public const string SectionName = "Protection";

    /// <summary>When true (the default), stops are checked on every quote and sold by <see cref="ProtectionService"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A quote older than this never triggers or prices a stop-loss sell.</summary>
    public TimeSpan MaxQuoteAge { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Least time between two tries on the same coin.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Tries in a row without a fill before the exit is reported blocked and retried every <see cref="BlockedRetryInterval"/>.</summary>
    public int QuickAttempts { get; set; } = 5;

    /// <summary>How often a blocked exit is tried again.</summary>
    public TimeSpan BlockedRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the stops are reloaded from the position plans and checked against the latest quotes.</summary>
    public TimeSpan PlanRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Protective orders across all coins in any one second; the exchange allows 10 order placements a second.</summary>
    public int MaxOrdersPerSecond { get; set; } = 5;

    public void Validate()
    {
        if (MaxQuoteAge < TimeSpan.FromSeconds(5) || MaxQuoteAge > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Protection:MaxQuoteAge must be between 5 seconds and 5 minutes.");
        if (RetryDelay < TimeSpan.FromSeconds(2) || RetryDelay > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Protection:RetryDelay must be between 2 seconds and 1 minute.");
        if (QuickAttempts is < 1 or > 5)
            throw new InvalidOperationException("Protection:QuickAttempts must be between 1 and 5.");
        if (BlockedRetryInterval < TimeSpan.FromSeconds(30) || BlockedRetryInterval > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Protection:BlockedRetryInterval must be between 30 seconds and 10 minutes.");
        if (PlanRefreshInterval < TimeSpan.FromSeconds(5) || PlanRefreshInterval > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Protection:PlanRefreshInterval must be between 5 seconds and 5 minutes.");
        if (MaxOrdersPerSecond is < 1 or > 9)
            throw new InvalidOperationException("Protection:MaxOrdersPerSecond must be between 1 and 9, below the exchange's 10.");
    }
}

/// <summary>A quote at or below a position's stop, as the quote listener saw it.</summary>
/// <param name="DetectedAt">When the engine saw it; minus the quote's own time, the detection latency.</param>
public sealed record StopHit(string Symbol, decimal Stop, MarketQuote Quote, DateTimeOffset DetectedAt);

/// <summary>
/// Watches every quote the engine receives for the armed stops, and queues a hit for <see cref="ProtectionService"/>. Holds no
/// other service, so the market-data poller can hand it quotes while the service asks the poller for fresh ones.
/// </summary>
public sealed class ProtectionTrigger(TimeProvider time) : IQuoteListener
{
    private readonly Lock _gate = new();
    private Dictionary<string, decimal> _stops = new(StringComparer.Ordinal);
    private readonly Channel<StopHit> _hits = Channel.CreateUnbounded<StopHit>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Hits waiting for the service, in the order they were seen.</summary>
    public ChannelReader<StopHit> Hits => _hits.Reader;

    /// <summary>Replaces the armed stops (symbol to stop price).</summary>
    public void Arm(IReadOnlyDictionary<string, decimal> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var copy = stops.Where(s => s.Value > 0).ToDictionary(s => MarketDataPoller.NormaliseSymbol(s.Key), s => s.Value, StringComparer.Ordinal);
        lock (_gate)
            _stops = copy;
    }

    /// <summary>Drops one coin's stop, once its position is gone: the next plan refresh arms it again if anything is still held.</summary>
    public void Disarm(string symbol)
    {
        var key = MarketDataPoller.NormaliseSymbol(symbol);
        lock (_gate)
        {
            if (_stops.ContainsKey(key))
                _stops = _stops.Where(s => s.Key != key).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>The armed stop for <paramref name="symbol"/>, if any.</summary>
    public decimal? StopFor(string symbol)
    {
        lock (_gate)
            return _stops.TryGetValue(MarketDataPoller.NormaliseSymbol(symbol), out var stop) ? stop : null;
    }

    public IReadOnlyDictionary<string, decimal> Armed
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, decimal>(_stops, StringComparer.Ordinal);
        }
    }

    /// <summary>Only a valid quote can trigger: an invalid or crossed book never starts a sell.</summary>
    public void OnQuote(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (!quote.IsValid || StopFor(quote.Symbol) is not { } stop || quote.Bid > stop)
            return;
        _hits.Writer.TryWrite(new StopHit(MarketDataPoller.NormaliseSymbol(quote.Symbol), stop, quote, time.GetUtcNow()));
    }
}

/// <summary>How an exit ended.</summary>
public enum ExitOutcome
{
    /// <summary>Nothing left to sell (all of it sold, or less than the exchange minimum left).</summary>
    Exited,

    /// <summary>The fresh order book no longer showed the bid at or below the stop before anything was sold: a wick, no sell.</summary>
    NotConfirmed,

    /// <summary>The bid went back above the stop after part was sold; the rest stays protected by the same stop.</summary>
    Recovered,

    /// <summary>The position no longer has a stop (its plan is gone).</summary>
    NoStop,

    /// <summary>This engine lost the engine lease: another engine of this mode protects the positions now.</summary>
    LeaseLost,

    /// <summary>The engine is stopping.</summary>
    Stopped,
}

/// <summary>An exit the engine keeps trying but can't complete: the dashboard and the logs show it.</summary>
/// <param name="Problem">A fixed code: <see cref="ProtectionService.Problems"/>.</param>
/// <param name="Attempts">Tries in a row without a fill, orders sent or not (a book too wide to sell into counts).</param>
public sealed record BlockedExit(string Symbol, DateTimeOffset Since, string Problem, int Attempts);

/// <summary>
/// Sells a position as soon as a valid, fresh quote shows its bid at or below its stop, independently of the strategy, the
/// scan and the agents. A hit is confirmed on a freshly fetched order book, then sold with an immediate-or-cancel limit order
/// priced at the emergency band below the mid (<see cref="RiskLimits.EmergencySlippageFraction"/>), through
/// <see cref="ExecutionService"/> and so the risk gate. Whatever didn't fill is sold again on a new book after
/// <see cref="ProtectionOptions.RetryDelay"/>; after <see cref="ProtectionOptions.QuickAttempts"/> tries in a row without a
/// fill the exit is reported blocked (journal, critical log, status) and tried every <see cref="ProtectionOptions.BlockedRetryInterval"/>
/// until it completes. The owner's pause and the safety halts don't stop it (the gate lets these sells through); only losing
/// the engine lease does.
/// </summary>
/// <remarks>
/// One exit per coin at a time, and each try only after the previous one is settled: an immediate-or-cancel order never
/// rests, so once it is answered nothing of it can still fill, and the next try sells what the fresh balance still shows.
/// A try whose outcome is unknown waits for the next one, which reads the balance again first, so a sell is never
/// repeated on coins already sold.
/// </remarks>
public sealed partial class ProtectionService : BackgroundService
{
    /// <summary>The order source, journal tool and entry kind of the engine's stop-loss sells.</summary>
    public const string Source = "protective-exit";

    /// <summary>The fixed codes a blocked or failed try reports; free text stays in the journal.</summary>
    public static readonly IReadOnlySet<string> Problems = new HashSet<string>(StringComparer.Ordinal)
    {
        "quote-unavailable", "quote-stale", "spread-too-wide", "ioc-unfilled", "ioc-rested", "coins-reserved", "orders-unavailable",
        "balances-unavailable", "rejected", "exchange-refused", "placement-uncertain",
    };

    private static readonly TimeSpan[] FillLookups = [TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    private readonly ExecutionService _execution;
    private readonly ProtectionTrigger _trigger;
    private readonly MarketState _market;
    private readonly Func<string, CancellationToken, Task<MarketQuote?>> _freshQuote;
    private readonly IJournal _journal;
    private readonly RiskLimits _limits;
    private readonly ProtectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ProtectionService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string?> _strategy;
    private readonly Func<string, decimal?>? _atr;
    private readonly Func<decimal?>? _stopAtr;
    private readonly Dictionary<string, decimal> _adopted = new(StringComparer.Ordinal); // set, not yet saved; RefreshAsync only
    private readonly HashSet<string> _waitingForAtr = new(StringComparer.Ordinal); // logged once each
    private readonly ConcurrentDictionary<string, Task<ExitOutcome>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BlockedExit> _blocked = new(StringComparer.Ordinal);
    private readonly Lock _startGate = new();
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly Queue<DateTimeOffset> _sentAt = new();
    private CancellationToken _stopping = CancellationToken.None;

    public ProtectionService(
        ExecutionService execution,
        ProtectionTrigger trigger,
        MarketState market,
        Func<string, CancellationToken, Task<MarketQuote?>> freshQuote,
        IJournal journal,
        RiskLimits limits,
        ProtectionOptions options,
        TimeProvider time,
        ILogger<ProtectionService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string?>? strategy = null,
        Func<string, decimal?>? atr = null,
        Func<decimal?>? stopAtr = null)
    {
        options.Validate();
        _execution = execution;
        _trigger = trigger;
        _market = market;
        _freshQuote = freshQuote;
        _journal = journal;
        _limits = limits;
        _options = options;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
        _strategy = strategy ?? (() => null);
        _atr = atr;
        _stopAtr = stopAtr;
    }

    /// <summary>Exits the engine keeps trying but couldn't complete, by coin.</summary>
    public IReadOnlyList<BlockedExit> Blocked => _blocked.Values.OrderBy(b => b.Symbol, StringComparer.Ordinal).ToList();

    /// <summary>Coins being sold now.</summary>
    public IReadOnlyList<string> Exiting => _running.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>The stops armed now, by coin.</summary>
    public IReadOnlyDictionary<string, decimal> Armed => _trigger.Armed;

    /// <summary>When the stops were last loaded from the position plans.</summary>
    public DateTimeOffset? ArmedAt { get; private set; }

    /// <summary>
    /// Reloads the stops from the position plans and checks each against the latest quote, so a stop already crossed (after
    /// a restart, or while quotes were stale) is sold as soon as a fresh quote shows it. Keeps the previous stops when the
    /// plans can't be loaded. Returns the exits started.
    /// </summary>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PositionPlan>? plans;
        try
        {
            plans = await PositionPlans.LoadAsync(_execution, _journal, cancellationToken, skipUnpriced: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPlansFailed(_logger, ex.GetType().Name, ex.Message);
            plans = null;
        }

        if (plans is not null)
        {
            try
            {
                plans = await AdoptStopsAsync(plans).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAdoptionFailed(_logger, ex.GetType().Name, ex.Message); // the stops the plans have are armed all the same
            }

            _trigger.Arm(plans.Where(p => p.StopLoss is > 0).ToDictionary(p => p.Symbol, p => p.StopLoss!.Value, StringComparer.Ordinal));
            ArmedAt = _time.GetUtcNow();
        }

        var started = 0;
        var now = _time.GetUtcNow();
        foreach (var (symbol, stop) in _trigger.Armed)
        {
            if (_market.Quote(symbol) is { IsValid: true } quote && now - quote.AsOf <= _options.MaxQuoteAge && quote.Bid <= stop
                && Start(new StopHit(symbol, stop, quote, now)))
                started++;
        }

        return started;
    }

    /// <summary>
    /// The stop for a position bought without one: <paramref name="stopAtr"/> hourly ATRs below the bid (the active strategy's
    /// rule for its own buys), and at least <see cref="Cix.Domain.Scanning.MarketScanner.MinExitDistanceFraction"/> below it,
    /// so the spread alone can't sell it. Null when that would be at or below zero.
    /// </summary>
    public static decimal? PlanlessStop(decimal bid, decimal atr, decimal stopAtr)
    {
        var stop = Math.Min(bid - (stopAtr * atr), bid * (1 - Cix.Domain.Scanning.MarketScanner.MinExitDistanceFraction));
        stop = Math.Round(stop, 8, MidpointRounding.ToZero);
        return stop > 0 ? stop : null;
    }

    /// <summary>
    /// Gives every position without a stop one (pre-live assessment F-02): a coin held before the engine traded it, the owner's
    /// own buy, an older buy without a stop. Set once, from the bid when the engine first sees the position priced and the
    /// hourly ATR (<see cref="PlanlessStop"/>), and saved in the position's plan, so a restart or a later price never moves it.
    /// A stop that couldn't be saved is armed from memory and saved at the next refresh. Without a fresh price or enough
    /// candles for the ATR, the position waits for the next refresh.
    /// </summary>
    private async Task<IReadOnlyList<PositionPlan>> AdoptStopsAsync(IReadOnlyList<PositionPlan> plans)
    {
        if (_atr is null || _stopAtr is null)
            return plans;
        foreach (var gone in _adopted.Keys.Concat(_waitingForAtr).Where(k => !plans.Any(p => p.Symbol == k)).ToList())
        {
            _adopted.Remove(gone);
            _waitingForAtr.Remove(gone);
        }

        var now = _time.GetUtcNow();
        var result = new List<PositionPlan>(plans.Count);
        foreach (var plan in plans)
        {
            if (plan.StopLoss is > 0)
            {
                _adopted.Remove(plan.Symbol);
                result.Add(plan);
                continue;
            }

            decimal stop;
            (decimal Stop, decimal Bid, decimal Atr, decimal StopAtr)? set = null;
            if (_adopted.TryGetValue(plan.Symbol, out var unsaved))
            {
                stop = unsaved;
            }
            else if (NewStop(plan.Symbol, now) is { } computed)
            {
                (stop, set) = (computed.Stop, computed);
            }
            else
            {
                result.Add(plan);
                continue;
            }

            var saved = _execution.AdoptStop(plan, stop, now, _strategy());
            if (saved is > 0)
            {
                _adopted.Remove(plan.Symbol);
                stop = saved.Value; // a stop the plan got meanwhile stays
            }
            else
            {
                _adopted[plan.Symbol] = stop;
            }

            if (set is { } first)
            {
                LogStopAdopted(_logger, plan.Symbol, stop, first.StopAtr, first.Atr, first.Bid);
                await RecordAsync("stop-adopted", Inv($"stop set for {plan.Symbol} at {stop:0.########}: {first.StopAtr:0.##} ATR below the bid, the position had none"),
                    new Dictionary<string, string>
                    {
                        ["symbol"] = plan.Symbol,
                        ["stopLoss"] = stop.ToString(CultureInfo.InvariantCulture),
                        ["bid"] = first.Bid.ToString(CultureInfo.InvariantCulture),
                        ["atr"] = first.Atr.ToString(CultureInfo.InvariantCulture),
                        ["stopAtr"] = first.StopAtr.ToString(CultureInfo.InvariantCulture),
                        ["saved"] = saved is > 0 ? "true" : "false",
                    }).ConfigureAwait(false);
            }

            result.Add(plan with { StopLoss = stop });
        }

        return result;
    }

    private (decimal Stop, decimal Bid, decimal Atr, decimal StopAtr)? NewStop(string symbol, DateTimeOffset now)
    {
        if (_market.Quote(symbol) is not { IsValid: true } quote || now - quote.AsOf > _options.MaxQuoteAge)
            return null; // priced again soon: positions without a fresh price aren't loaded at all
        decimal? atr, stopAtr;
        try
        {
            atr = _atr!(symbol);
            stopAtr = _stopAtr!();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_waitingForAtr.Add(symbol))
                LogNoAdoptedStop(_logger, symbol, ex.GetType().Name + ": " + ex.Message);
            return null;
        }

        if (atr is not > 0 || stopAtr is not > 0 || PlanlessStop(quote.Bid, atr.Value, stopAtr.Value) is not { } stop)
        {
            if (_waitingForAtr.Add(symbol))
                LogNoAdoptedStop(_logger, symbol, "no hourly ATR yet (too few fresh candles) or no stop distance in the strategy");
            return null;
        }

        _waitingForAtr.Remove(symbol);
        return (stop, quote.Bid, atr.Value, stopAtr.Value);
    }

    /// <summary>Starts an exit for the hit's coin unless one is running already (so a repeated hit can't sell twice). Returns whether it started.</summary>
    public bool Start(StopHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        lock (_startGate)
        {
            // A hit queued before the coin's exit ended (and disarmed it) is stale: the next plan refresh re-arms what is still held.
            if (_running.ContainsKey(hit.Symbol) || _trigger.StopFor(hit.Symbol) is null)
                return false;
            _running[hit.Symbol] = RunTrackedAsync(hit);
            return true;
        }
    }

    /// <summary>Waits for every running exit to end (for tests and shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    private async Task<ExitOutcome> RunTrackedAsync(StopHit hit)
    {
        await Task.Yield(); // runs apart from the caller, which may be the quote loop
        try
        {
            return await RunExitAsync(hit, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExitOutcome.Stopped;
        }
#pragma warning disable CA1031 // an exit that crashed must not take the service down: the next refresh starts it again
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogExitCrashed(_logger, hit.Symbol, ex.GetType().Name, ex.Message);
            return ExitOutcome.Stopped;
        }
        finally
        {
            lock (_startGate) // after Start has registered it
                _running.TryRemove(hit.Symbol, out _);
        }
    }

    /// <summary>One coin's exit, from the hit until nothing is left to sell (or the stop no longer applies).</summary>
    public async Task<ExitOutcome> RunExitAsync(StopHit hit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var symbol = hit.Symbol;
        var started = _time.GetUtcNow();
        var tries = 0;
        var failures = 0;
        var cancelledOwn = false;
        decimal soldBase = 0m, soldQuote = 0m;
        DateTimeOffset? firstFillAt = null;
        LogHit(_logger, symbol, hit.Quote.Bid, hit.Stop, (hit.DetectedAt - hit.Quote.AsOf).TotalMilliseconds);

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return ExitOutcome.Stopped;
            if (!_execution.HoldsLease)
                return await EndAsync(ExitOutcome.LeaseLost).ConfigureAwait(false);
            if (_trigger.StopFor(symbol) is not { } stop)
                return await EndAsync(soldBase > 0 ? ExitOutcome.Exited : ExitOutcome.NoStop).ConfigureAwait(false);

            var (problem, detail, filled) = await TryOnceAsync().ConfigureAwait(false);
            if (problem == "done")
            {
                _trigger.Disarm(symbol); // nothing held: a hit still queued, or the next quote, mustn't start an exit for a sold coin
                return await EndAsync(ExitOutcome.Exited).ConfigureAwait(false);
            }
            if (problem == "above-stop")
                return await EndAsync(soldBase > 0 || tries > 0 ? ExitOutcome.Recovered : ExitOutcome.NotConfirmed).ConfigureAwait(false);

            if (filled)
            {
                failures = 0;
                if (_blocked.TryRemove(symbol, out _))
                    LogUnblocked(_logger, symbol);
            }
            else if (problem is not null)
            {
                failures++;
                if (failures >= _options.QuickAttempts)
                {
                    var since = _blocked.TryGetValue(symbol, out var was) ? was.Since : _time.GetUtcNow();
                    _blocked[symbol] = new BlockedExit(symbol, since, problem, failures);
                    if (was is null)
                    {
                        LogBlocked(_logger, symbol, failures, problem, detail ?? problem);
                        await RecordAsync("exit-blocked", Inv($"exit blocked {symbol} after {failures} tries: {problem}"), new Dictionary<string, string>
                        {
                            ["symbol"] = symbol,
                            ["problem"] = problem,
                            ["detail"] = detail ?? problem,
                            ["attempts"] = Inv($"{failures}"),
                            ["ordersSent"] = Inv($"{tries}"),
                            ["stop"] = Inv($"{stop}"),
                        }).ConfigureAwait(false);
                    }

                    await _delay(_options.BlockedRetryInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            await _delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);

            // One try: a fresh book, the coins freed and counted, then an immediate-or-cancel sell. Returns a problem code
            // (null when it sold something), "done" when nothing is left, or "above-stop" when the book no longer confirms.
            async Task<(string? Problem, string? Detail, bool Filled)> TryOnceAsync()
            {
                MarketQuote? quote;
                try
                {
                    quote = await _freshQuote(symbol, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return ("quote-unavailable", ex.GetType().Name, false);
                }

                var now = _time.GetUtcNow();
                if (quote is null || !quote.IsValid || now - quote.AsOf > _options.MaxQuoteAge || quote.AsOf > now)
                    return ("quote-stale", "no valid fresh order book", false);
                if (quote.Bid > stop)
                    return ("above-stop", null, false);

                if (!cancelledOwn)
                {
                    try
                    {
                        await _execution.CancelOwnOrdersOnAsync(symbol, cancellationToken).ConfigureAwait(false);
                        cancelledOwn = true;
                    }
                    catch (ExchangeException ex)
                    {
                        return ("orders-unavailable", ex.GetType().Name, false);
                    }
                }

                SellableNow now_;
                try
                {
                    now_ = await _execution.SellableNowAsync(symbol, cancellationToken).ConfigureAwait(false);
                }
                catch (PortfolioUnavailableException ex)
                {
                    return ("balances-unavailable", ex.Message, false);
                }

                if (!now_.HoldsPosition)
                    return ("done", null, false);
                var sellable = now_.Sellable;
                if (sellable <= 0)
                {
                    // Held, but nothing free: an open sell (one the cancel pass missed, or placed since) holds the coins. Cancel
                    // the engine's orders again next time rather than call the exit finished with the position still on.
                    cancelledOwn = false;
                    return ("coins-reserved", "the coins are held but reserved by an open order", false);
                }

                // As low as the emergency band allows below the mid: the exchange fills at the bids above it.
                var floor = quote.Mid * (1 - _limits.EmergencySlippageFraction);
                if (quote.Bid < floor)
                    return ("spread-too-wide", Inv($"bid {quote.Bid} is below the band floor {floor:0.########} ({_limits.EmergencySlippageFraction:P1} under mid {quote.Mid})"), false);

                tries++;
                var clientOrderId = Guid.NewGuid().ToString();
                var sendAt = _time.GetUtcNow();
                var result = await _execution.PlaceAsync(
                    new OrderIntent(symbol, OrderSide.Sell, sellable, floor, clientOrderId, OrderPurpose.ProtectiveExit, TimeInForce.ImmediateOrCancel),
                    postOnly: false, cancellationToken, countsTowardRejectStreak: false,
                    context: new OrderContext(Source, Strategy: _strategy(), ExitReason: ExitReasons.Stop),
                    beforeSend: async ct => // paced at the send itself, not at the door of the placement lock
                    {
                        await PaceAsync(ct).ConfigureAwait(false);
                        sendAt = _time.GetUtcNow();
                    }).ConfigureAwait(false);
                var answeredAt = _time.GetUtcNow();

                ExchangeOrder? order = null;
                if (result is { Status: ExecutionStatus.Placed, Placed: { } placed })
                    order = await LookUpAsync(placed.VenueOrderId, cancellationToken).ConfigureAwait(false);
                var seenAt = _time.GetUtcNow();
                var filledBase = order?.FilledQuantity ?? 0m;
                var filledQuote = order?.FilledAmountQuote ?? 0m;
                var average = filledBase > 0 ? filledQuote / filledBase : (decimal?)null;
                if (filledBase > 0)
                {
                    soldBase += filledBase;
                    soldQuote += filledQuote;
                    firstFillAt ??= seenAt;
                }

                // An order the exchange left open (immediate-or-cancel not honoured) or one that couldn't be read back may be
                // resting with the coins reserved. The next try cancels the engine's orders on the coin again before it counts
                // what is left, so a resting sell is never mistaken for a finished exit.
                if (result.Status is ExecutionStatus.Placed or ExecutionStatus.Uncertain && (order is null || order.IsOpen))
                    cancelledOwn = false;

                var (code, why) = result.Status switch
                {
                    ExecutionStatus.Placed when order is null => ("placement-uncertain", "placed, but the order couldn't be read back"),
                    ExecutionStatus.Placed when order is { IsOpen: true } => ("ioc-rested", "the exchange left the rest of the order open; it is cancelled before the next try"),
                    ExecutionStatus.Placed when filledBase <= 0 => ("ioc-unfilled", "no bids within the emergency band"),
                    ExecutionStatus.Placed => ((string?)null, (string?)null),
                    ExecutionStatus.Uncertain => ("placement-uncertain", result.Reason),
                    ExecutionStatus.Rejected => ("rejected", $"{result.Rule}: {result.Reason}"),
                    _ => ("exchange-refused", result.Rule),
                };

                var data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool"] = Source,
                    ["clientOrderId"] = clientOrderId,
                    ["symbol"] = symbol,
                    ["side"] = "sell",
                    ["attempt"] = Inv($"{tries}"),
                    ["stop"] = Inv($"{stop}"),
                    ["triggerBid"] = Inv($"{hit.Quote.Bid}"),
                    ["bid"] = Inv($"{quote.Bid}"),
                    ["mid"] = Inv($"{quote.Mid}"),
                    ["requestedSize"] = Inv($"{sellable}"),
                    ["requestedPrice"] = Inv($"{result.Order?.LimitPrice ?? floor}"),
                    ["status"] = result.Status.ToString().ToLowerInvariant(),
                    ["rule"] = result.Rule,
                    ["reason"] = result.Reason,
                    ["filledSize"] = Inv($"{filledBase}"),
                    ["sentAt"] = sendAt.ToString("O", CultureInfo.InvariantCulture),
                    ["detectionMs"] = Ms(hit.DetectedAt - hit.Quote.AsOf),
                    ["confirmMs"] = Ms(quote.AsOf - hit.DetectedAt),
                    ["submitMs"] = Ms(answeredAt - sendAt),
                    ["fillMs"] = Ms(seenAt - answeredAt),
                    ["sinceHitMs"] = Ms(answeredAt - hit.DetectedAt),
                    ["rationale"] = Inv($"stop-loss {symbol}: bid {quote.Bid} at or below stop {stop}"),
                };
                if (average is { } avg)
                {
                    data["averagePrice"] = Inv($"{Math.Round(avg, 8)}");
                    data["slippageVsStop"] = Inv($"{Math.Round((stop - avg) / stop, 6)}");
                    data["slippageVsTriggerBid"] = Inv($"{Math.Round((hit.Quote.Bid - avg) / hit.Quote.Bid, 6)}");
                }

                if (code is not null)
                    data["problem"] = code;
                await RecordAsync("order", Inv($"protective stop sell {sellable} {symbol} @ {floor:0.########} (try {tries}): {result.Status} ({result.Rule}), filled {filledBase}"), data)
                    .ConfigureAwait(false);
                LogTry(_logger, symbol, tries, sellable, result.Status.ToString(), result.Rule, filledBase, (answeredAt - sendAt).TotalMilliseconds);
                return (code, why, filledBase > 0);
            }
        }

        async Task<ExitOutcome> EndAsync(ExitOutcome outcome)
        {
            _blocked.TryRemove(symbol, out _);
            var ended = _time.GetUtcNow();
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = symbol,
                ["outcome"] = outcome.ToString().ToLowerInvariant(),
                ["stop"] = Inv($"{hit.Stop}"),
                ["triggerBid"] = Inv($"{hit.Quote.Bid}"),
                ["tries"] = Inv($"{tries}"),
                ["soldSize"] = Inv($"{soldBase}"),
                ["soldQuote"] = Inv($"{soldQuote}"),
                ["detectionMs"] = Ms(hit.DetectedAt - hit.Quote.AsOf),
                ["durationMs"] = Ms(ended - started),
            };
            if (firstFillAt is { } first)
                data["firstFillMs"] = Ms(first - hit.DetectedAt);
            if (soldBase > 0)
            {
                var avg = soldQuote / soldBase;
                data["averagePrice"] = Inv($"{Math.Round(avg, 8)}");
                data["slippageVsStop"] = Inv($"{Math.Round((hit.Stop - avg) / hit.Stop, 6)}");
                data["slippageVsTriggerBid"] = Inv($"{Math.Round((hit.Quote.Bid - avg) / hit.Quote.Bid, 6)}");
            }

            // Nothing tried and nothing to sell is just a coin already sold: no entry for it.
            if (tries > 0 || outcome == ExitOutcome.NotConfirmed)
                await RecordAsync(Source, Inv($"stop-loss exit {symbol}: {outcome.ToString().ToLowerInvariant()}, sold {soldBase} in {tries} tries"), data).ConfigureAwait(false);
            LogEnded(_logger, symbol, outcome.ToString(), soldBase, tries, (ended - started).TotalMilliseconds);
            return outcome;
        }
    }

    /// <summary>Reads a just-placed order back, a few times: the exchange may not list it at once.</summary>
    private async Task<ExchangeOrder?> LookUpAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        foreach (var wait in FillLookups)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            try
            {
                if (await _execution.GetOrderAsync(venueOrderId, cancellationToken).ConfigureAwait(false) is { } order)
                    return order;
            }
            catch (ExchangeException)
            {
                // try again; the next try reads the balance regardless
            }
        }

        return null;
    }

    /// <summary>At most <see cref="ProtectionOptions.MaxOrdersPerSecond"/> protective orders in any second, across all coins.</summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _time.GetUtcNow();
                while (_sentAt.Count > 0 && now - _sentAt.Peek() >= TimeSpan.FromSeconds(1))
                    _sentAt.Dequeue();
                if (_sentAt.Count < _options.MaxOrdersPerSecond)
                {
                    _sentAt.Enqueue(now);
                    return;
                }

                var wait = _sentAt.Peek() + TimeSpan.FromSeconds(1) - now;
                await _delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _pace.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var nextRefresh = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_time.GetUtcNow() >= nextRefresh)
            {
                try
                {
                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPlansFailed(_logger, ex.GetType().Name, ex.Message);
                }

                // After a start, before the first quotes, held coins have no price and so no armed stop: try again sooner,
                // rather than leave them unwatched for a whole refresh interval.
                var waitingForQuotes = ArmedAt is null || (_trigger.Armed.Count == 0 && _market.QuoteTimes().Count == 0);
                nextRefresh = _time.GetUtcNow() + (waitingForQuotes ? TimeSpan.FromSeconds(5) : _options.PlanRefreshInterval);
            }

            // Wake on the next hit, or at the next refresh (on the engine's clock, so tests can drive it).
            var left = nextRefresh - _time.GetUtcNow();
            using var timer = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1), _time);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timer.Token);
            try
            {
                if (await _trigger.Hits.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                {
                    while (_trigger.Hits.TryRead(out var hit))
                        Start(hit);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // time to refresh
            }
        }

        await WhenIdleAsync().ConfigureAwait(false);
    }

    private async Task RecordAsync(string kind, string summary, IReadOnlyDictionary<string, string> data)
    {
        try
        {
            var now = _time.GetUtcNow();
            await _journal.AppendAsync(new JournalEntry(JournalIds.New(now, kind), now, kind, "engine", null, _strategy(), summary, data), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalFailed(_logger, kind, ex.Message); // never stops a sell
        }
    }

    private static string Ms(TimeSpan span) => Math.Max(0, (long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stop hit {Symbol}: bid {Bid} at or below stop {Stop}, detected {DetectionMs} ms after the quote")]
    private static partial void LogHit(ILogger logger, string symbol, decimal bid, decimal stop, double detectionMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stop-loss sell {Symbol} try {Try}: {Size} {Status} ({Rule}), filled {Filled}, answered in {SubmitMs} ms")]
    private static partial void LogTry(ILogger logger, string symbol, int @try, decimal size, string status, string rule, decimal filled, double submitMs);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Exit blocked for {Symbol} after {Failures} tries without a fill ({Problem}): {Detail}. Retrying every minute.")]
    private static partial void LogBlocked(ILogger logger, string symbol, int failures, string problem, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exit for {Symbol} is selling again")]
    private static partial void LogUnblocked(ILogger logger, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stop-loss exit {Symbol} ended {Outcome}: sold {Sold} in {Tries} tries over {DurationMs} ms")]
    private static partial void LogEnded(ILogger logger, string symbol, string outcome, decimal sold, int tries, double durationMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stop-loss exit for {Symbol} failed ({Error}): {Detail}; the next refresh starts it again")]
    private static partial void LogExitCrashed(ILogger logger, string symbol, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stops not reloaded ({Error}): {Detail}; the previous stops stay armed")]
    private static partial void LogPlansFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stop set for {Symbol}, which had none: {Stop}, {StopAtr} ATR ({Atr}) below the bid {Bid}")]
    private static partial void LogStopAdopted(ILogger logger, string symbol, decimal stop, decimal stopAtr, decimal atr, decimal bid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No stop yet for {Symbol}, which has none: {Detail}; tried again at the next refresh")]
    private static partial void LogNoAdoptedStop(ILogger logger, string symbol, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stops for positions without one not set ({Error}): {Detail}; the plans' own stops are armed")]
    private static partial void LogAdoptionFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal entry {Kind} not written: {Detail}")]
    private static partial void LogJournalFailed(ILogger logger, string kind, string detail);
}
