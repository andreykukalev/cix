using Cix.Domain.Exchange;

namespace Cix.Stocks.Simulation;

/// <summary>
/// Costs of carrying out advice by hand at the broker. The defaults are deliberately pessimistic placeholders, not
/// Revolut's tariff: set them from the owner's plan.
/// </summary>
public sealed record SimulationSettings
{
    /// <summary>Commission as a fraction of the order's value in the base currency.</summary>
    public decimal CommissionFraction { get; init; } = 0.0025m;

    /// <summary>The least commission an order pays, in the base currency.</summary>
    public decimal MinCommission { get; init; } = 1m;

    /// <summary>Currency conversion fee on trades in a currency other than the base, as a fraction of the value.</summary>
    public decimal FxFeeFraction { get; init; } = 0.0025m;

    /// <summary>Price given up on every fill (spread and the time it takes the owner to act), as a fraction.</summary>
    public decimal SlippageFraction { get; init; } = 0.001m;

    public SimulationSettings Validate()
    {
        foreach (var (value, name) in new[]
                 {
                     (CommissionFraction, nameof(CommissionFraction)), (FxFeeFraction, nameof(FxFeeFraction)),
                     (SlippageFraction, nameof(SlippageFraction)),
                 })
        {
            if (value < 0 || value > 0.05m)
                throw new ArgumentOutOfRangeException(name, value, "Must be between 0 and 0.05.");
        }

        if (MinCommission < 0)
            throw new ArgumentOutOfRangeException(nameof(MinCommission), MinCommission, "Can't be negative.");
        return this;
    }
}

public enum TradeKind
{
    Buy,
    Sell,

    /// <summary>The stop at the broker sold: at the stop, or at the open when the price gapped under it.</summary>
    StopOut,
}

/// <summary>A simulated fill.</summary>
/// <param name="Price">The fill price in the instrument's currency, slippage included.</param>
/// <param name="FeesBase">Commission and FX fee, in the base currency.</param>
/// <param name="ProfitBase">For sells: proceeds less cost, fees on both sides included, in the base currency.</param>
public sealed record PaperTrade(
    DateOnly Day, string Symbol, TradeKind Kind, decimal Quantity, decimal Price, decimal FeesBase, decimal? ProfitBase, string Reason);

/// <summary>A paper position: what the owner would hold, and what it cost in the base currency, fees included.</summary>
public sealed record PaperPosition(Holding Holding, decimal CostBase);

/// <summary>Why a recommendation wasn't carried out the next day.</summary>
public sealed record SkippedAction(DateOnly Day, string Symbol, ActionKind Kind, string Reason);

/// <summary>
/// A portfolio that carries out the advisor's recommendations as the owner would: the advice is computed after a day's
/// close and carried out at the next day's open, and a stop rests at the broker, selling on the day the low reaches it at
/// the stop, or at the open when the price opens under it (a gap). FX rates are fixed for the run.
/// </summary>
public sealed class PaperPortfolio
{
    /// <summary>Skip reasons: stable strings for the report.</summary>
    public static class Skips
    {
        public const string NotHeld = "not-held";
        public const string AlreadyHeld = "already-held";
        public const string OpenUnderStop = "open-under-stop";
        public const string NoCash = "no-cash";
        public const string NoFxRate = "no-fx-rate";
    }

    private readonly string _baseCurrency;
    private readonly SimulationSettings _settings;
    private readonly Dictionary<string, Instrument> _instruments;
    private readonly IReadOnlyDictionary<string, decimal> _fxToBase;
    private readonly Dictionary<string, PaperPosition> _positions = new(StringComparer.Ordinal);
    private readonly List<PaperTrade> _trades = [];
    private readonly List<SkippedAction> _skipped = [];

    public PaperPortfolio(
        string baseCurrency, decimal cash, IEnumerable<Instrument> instruments, IReadOnlyDictionary<string, decimal> fxToBase,
        SimulationSettings settings)
        : this(baseCurrency, cash, [], instruments, fxToBase, settings)
    {
    }

    /// <summary>A portfolio picked up where a saved one left off, with its cash and positions.</summary>
    public PaperPortfolio(
        string baseCurrency, decimal cash, IEnumerable<PaperPosition> positions, IEnumerable<Instrument> instruments,
        IReadOnlyDictionary<string, decimal> fxToBase, SimulationSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(cash);
        _baseCurrency = baseCurrency;
        Cash = cash;
        _instruments = instruments.ToDictionary(i => i.Symbol, StringComparer.Ordinal);
        _fxToBase = fxToBase;
        _settings = settings.Validate();
        foreach (var position in positions)
        {
            if (position.Holding.Quantity <= 0 || position.CostBase < 0 || !_positions.TryAdd(position.Holding.Symbol, position))
                throw new ArgumentException($"The saved position in {position.Holding.Symbol} is empty, negative or twice.", nameof(positions));
        }
    }

    public decimal Cash { get; private set; }

    public IReadOnlyList<Holding> Holdings => _positions.Values.Select(p => p.Holding).OrderBy(h => h.Symbol, StringComparer.Ordinal).ToList();

    /// <summary>The positions with their cost, to save and pick up again.</summary>
    public IReadOnlyList<PaperPosition> Positions => _positions.Values.OrderBy(p => p.Holding.Symbol, StringComparer.Ordinal).ToList();

    public IReadOnlyList<PaperTrade> Trades => _trades;

    public IReadOnlyList<SkippedAction> Skipped => _skipped;

    /// <summary>
    /// One trading day: first the advice from the previous close, at the open (sells, then stops, then buys, as the advice
    /// lists them), then the stops resting at the broker against the day's low. Symbols without a bar today (their exchange
    /// was closed) carry their advice to their next bar.
    /// </summary>
    /// <returns>The advice not carried out because its symbol had no bar today.</returns>
    public IReadOnlyList<Recommendation> ExecuteDay(DateOnly day, IReadOnlyDictionary<string, Candle> bars, IReadOnlyList<Recommendation> advice)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(advice);
        var waiting = new List<Recommendation>();

        foreach (var action in advice.OrderBy(a => a.Kind))
        {
            if (!bars.TryGetValue(action.Symbol, out var bar))
            {
                waiting.Add(action);
                continue;
            }

            Carry(day, action, bar);
        }

        foreach (var symbol in _positions.Keys.ToList())
        {
            var holding = _positions[symbol].Holding;
            if (holding.Stop is not { } stop || !bars.TryGetValue(symbol, out var bar) || bar.Low > stop)
                continue;

            Sell(day, symbol, Math.Min(bar.Open, stop), TradeKind.StopOut, "stop");
        }

        return waiting;
    }

    /// <summary>Cash plus positions at the given prices (in their currencies), in the base currency.</summary>
    public decimal Equity(IReadOnlyDictionary<string, decimal> lastClose)
    {
        var equity = Cash;
        foreach (var (symbol, position) in _positions)
        {
            var price = lastClose.TryGetValue(symbol, out var p) ? p : position.Holding.AverageCost;
            equity += position.Holding.Quantity * price * (Fx(symbol) ?? 0m);
        }

        return equity;
    }

    private void Carry(DateOnly day, Recommendation action, Candle bar)
    {
        var held = _positions.TryGetValue(action.Symbol, out var position);
        switch (action.Kind)
        {
            case ActionKind.Sell when held:
                Sell(day, action.Symbol, bar.Open, TradeKind.Sell, action.Reason);
                break;
            case ActionKind.SetStop or ActionKind.RaiseStop when held:
                if (action.Stop is not { } stop)
                    break;
                // Raised only, as the advisor promises; a stop above the open would sell at once, which the stop check does.
                if (position!.Holding.Stop is not { } current || stop > current)
                    _positions[action.Symbol] = position with { Holding = position.Holding with { Stop = stop } };
                break;
            case ActionKind.Buy when held:
                _skipped.Add(new(day, action.Symbol, action.Kind, Skips.AlreadyHeld));
                break;
            case ActionKind.Buy:
                Buy(day, action, bar);
                break;
            default:
                _skipped.Add(new(day, action.Symbol, action.Kind, Skips.NotHeld));
                break;
        }
    }

    private void Buy(DateOnly day, Recommendation action, Candle bar)
    {
        if (action.Stop is { } stop && bar.Open <= stop)
        {
            _skipped.Add(new(day, action.Symbol, action.Kind, Skips.OpenUnderStop));
            return;
        }

        if (Fx(action.Symbol) is not { } fx)
        {
            _skipped.Add(new(day, action.Symbol, action.Kind, Skips.NoFxRate));
            return;
        }

        var price = bar.Open * (1 + _settings.SlippageFraction);
        var step = _instruments.TryGetValue(action.Symbol, out var instrument) ? instrument.QuantityStep : 1m;
        // The open can be higher than the close the advice was sized on: buy what the cash pays for, never on credit. Fees are
        // at most the minimum commission plus both fractions of the value, so this always fits.
        var affordable = (Cash - _settings.MinCommission) / (price * fx * (1 + _settings.CommissionFraction + _settings.FxFeeFraction));
        var quantity = Advisor.FloorTo(Math.Min(action.Quantity, affordable), step);

        if (quantity <= 0)
        {
            _skipped.Add(new(day, action.Symbol, action.Kind, Skips.NoCash));
            return;
        }

        var value = quantity * price * fx;
        var fees = Fees(action.Symbol, value);
        Cash -= value + fees;
        _positions[action.Symbol] = new PaperPosition(new Holding(action.Symbol, quantity, price, action.Stop, day), value + fees);
        _trades.Add(new PaperTrade(day, action.Symbol, TradeKind.Buy, quantity, price, fees, null, action.Reason));
    }

    private void Sell(DateOnly day, string symbol, decimal at, TradeKind kind, string reason)
    {
        var position = _positions[symbol];
        var fx = Fx(symbol) ?? 0m;
        var price = at * (1 - _settings.SlippageFraction);
        var value = position.Holding.Quantity * price * fx;
        // A minimum commission larger than the proceeds would leave cash negative: the broker takes at most the proceeds.
        var fees = Math.Min(Fees(symbol, value), value);
        Cash += value - fees;
        _positions.Remove(symbol);
        _trades.Add(new PaperTrade(day, symbol, kind, position.Holding.Quantity, price, fees, value - fees - position.CostBase, reason));
    }

    private decimal Fees(string symbol, decimal valueBase)
    {
        var commission = Math.Max(_settings.MinCommission, valueBase * _settings.CommissionFraction);
        var foreign = _instruments.TryGetValue(symbol, out var i) && !string.Equals(i.Currency, _baseCurrency, StringComparison.Ordinal);
        return commission + (foreign ? valueBase * _settings.FxFeeFraction : 0m);
    }

    private decimal? Fx(string symbol)
    {
        if (!_instruments.TryGetValue(symbol, out var instrument) || string.Equals(instrument.Currency, _baseCurrency, StringComparison.Ordinal))
            return 1m;
        return _fxToBase.TryGetValue(instrument.Currency, out var rate) && rate > 0 ? rate : null;
    }
}
