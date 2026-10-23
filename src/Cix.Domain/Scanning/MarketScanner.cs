using System.Globalization;
using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Domain.Scanning;

/// <summary>
/// Scanner settings: filters, cost assumptions, score weights and sizing. Fractions are ratios (0.002 = 0.2%); amounts are in
/// the quote currency. Defaults suit EUR pairs with a €200 paper allocation (ADR-0006).
/// </summary>
public sealed record ScannerParameters
{
    /// <summary>Least 24h traded value (sum of hourly volume × close) for a symbol to be considered liquid.</summary>
    public decimal MinQuoteVolume24h { get; init; } = 100_000m;

    public decimal MaxSpreadFraction { get; init; } = 0.002m;

    /// <summary>Hourly ATR above this share of price is too wild for the stop sizes used here.</summary>
    public decimal MaxAtrFraction { get; init; } = 0.05m;

    /// <summary>Hourly ATR must be at least this multiple of the round-trip cost, so a move can pay for the trade.</summary>
    public decimal MinAtrToCostRatio { get; init; } = 2m;

    public decimal TakerFeeRate { get; init; } = 0.0009m;

    /// <summary>Allowance for filling worse than the quote, per side.</summary>
    public decimal SlippageFraction { get; init; } = 0.0005m;

    public decimal MomentumWeight { get; init; } = 1m;
    public decimal TrendWeight { get; init; } = 1m;
    public decimal VolumeWeight { get; init; } = 0.5m;
    public decimal CostWeight { get; init; } = 2m;

    /// <summary>Entry only on a pullback: price at most this many ATRs above EMA 20.</summary>
    public decimal EntryMaxAboveEma20Atr { get; init; } = 0.5m;

    public decimal StopAtr { get; init; } = 1.5m;
    public decimal RewardRisk { get; init; } = 2m;

    /// <summary>Most a stopped-out trade should lose, costs included. Sizing aims for this, then applies <see cref="OrderQuote"/>.</summary>
    public decimal RiskPerTradeQuote { get; init; } = 1m;

    /// <summary>Largest order the scanner suggests (the risk gate still has the final say).</summary>
    public decimal OrderQuote { get; init; } = 5m;

    public int MaxEntrySignals { get; init; } = 2;

    /// <summary>Hourly candles needed in the last 24 hours to judge liquidity.</summary>
    public int MinHourlyCandles24h { get; init; } = 12;

    public void Validate()
    {
        if (MinQuoteVolume24h < 0 || MaxSpreadFraction <= 0 || MaxAtrFraction <= 0 || MinAtrToCostRatio <= 0)
            throw new InvalidOperationException("Scanner filters must be positive.");
        if (TakerFeeRate < 0 || SlippageFraction < 0 || TakerFeeRate >= 0.1m || SlippageFraction >= 0.1m)
            throw new InvalidOperationException("Scanner fee and slippage must be between 0 and 0.1.");
        if (MomentumWeight < 0 || TrendWeight < 0 || VolumeWeight < 0 || CostWeight < 0)
            throw new InvalidOperationException("Scanner weights can't be negative.");
        if (EntryMaxAboveEma20Atr < 0 || StopAtr <= 0 || RewardRisk <= 0 || RiskPerTradeQuote <= 0 || OrderQuote <= 0)
            throw new InvalidOperationException("Scanner entry, stop, reward and sizing values must be positive.");
        if (MaxEntrySignals is < 0 or > 10 || MinHourlyCandles24h is < 1 or > 24)
            throw new InvalidOperationException("Scanner MaxEntrySignals must be 0-10 and MinHourlyCandles24h 1-24.");
    }
}

/// <summary>A position the account holds, with the plan recorded when it was bought (null when unknown).</summary>
public sealed record HeldPosition(string Symbol, decimal SellableBaseSize, decimal? StopLoss, decimal? TakeProfit);

/// <summary>Everything the scanner needs about one universe symbol. <paramref name="Hourly"/> is oldest first.</summary>
public sealed record ScanInput(
    string Symbol,
    FeatureSet Features,
    IReadOnlyList<Candle> Hourly,
    PairInfo? Pair,
    bool Allowed,
    bool HasOpenOrder);

public sealed record ScanRow(
    string Symbol,
    int Rank,
    decimal Score,
    decimal Price,
    Trend Trend,
    decimal AtrFraction,
    decimal? Return1h,
    decimal? Return24h,
    decimal QuoteVolume24h,
    decimal? VolumeRatio,
    decimal SpreadFraction,
    decimal RoundTripCostFraction,
    string? NoEntryReason);

public sealed record ScanExclusion(string Symbol, string Reason, string Detail);

/// <summary>A suggested buy: a limit at the ask, with the stop and target to pass to place_order.</summary>
public sealed record EntrySignal(
    string Symbol,
    int Rank,
    decimal LimitPrice,
    decimal BaseSize,
    decimal QuoteSize,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskQuote,
    string Reason);

/// <summary>A suggested exit: sell the whole sellable size with a limit at the bid.</summary>
public sealed record ExitSignal(string Symbol, decimal BaseSize, decimal LimitPrice, string Reason, string Detail);

public sealed record ScanResult(
    DateTimeOffset AsOf,
    IReadOnlyList<ScanRow> Ranked,
    IReadOnlyList<ScanExclusion> Excluded,
    IReadOnlyList<EntrySignal> Entries,
    IReadOnlyList<ExitSignal> Exits);

/// <summary>
/// Deterministic market scan (ADR-0006): filters a curated universe for liquidity, spread, volatility and cost, ranks what
/// passes by risk-adjusted momentum, suggests sized entries for the best pullbacks in uptrends, and flags held positions
/// that hit their stop, target or a downtrend. Pure arithmetic: no I/O, same inputs give the same result.
/// </summary>
public static class MarketScanner
{
    /// <summary>Momentum is the 24h return measured in hourly ATRs, clamped so one spike can't dominate.</summary>
    public const decimal MomentumClamp = 5m;

    public static ScanResult Scan(
        IReadOnlyList<ScanInput> inputs,
        IReadOnlyList<HeldPosition> held,
        ScannerParameters parameters,
        DateTimeOffset now,
        bool allowEntries = true)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(parameters);

        var heldBySymbol = held.ToDictionary(h => h.Symbol, StringComparer.Ordinal);
        var excluded = new List<ScanExclusion>();
        var eligible = new List<(ScanInput Input, Metrics Metrics, decimal Score)>();

        foreach (var input in inputs.OrderBy(i => i.Symbol, StringComparer.Ordinal))
        {
            var (metrics, exclusion) = Evaluate(input, parameters);
            if (exclusion is not null)
                excluded.Add(exclusion);
            else
                eligible.Add((input, metrics!, Score(input.Features, metrics!, parameters)));
        }

        var ranked = new List<ScanRow>();
        var entries = new List<EntrySignal>();
        var rank = 0;
        foreach (var (input, metrics, score) in eligible.OrderByDescending(e => e.Score).ThenBy(e => e.Input.Symbol, StringComparer.Ordinal))
        {
            rank++;
            string? noEntry;
            if (!allowEntries)
                noEntry = "trading halted";
            else if (heldBySymbol.ContainsKey(input.Symbol))
                noEntry = "already held";
            else if (input.HasOpenOrder)
                noEntry = "open order";
            else if (entries.Count >= parameters.MaxEntrySignals)
                noEntry = "signal limit reached";
            else
            {
                var (signal, reason) = Entry(input, metrics, rank, parameters);
                noEntry = reason;
                if (signal is not null)
                    entries.Add(signal);
            }

            var f = input.Features;
            ranked.Add(new ScanRow(
                input.Symbol, rank, Math.Round(score, 3, MidpointRounding.AwayFromZero), f.Price!.Value, f.Trend, f.AtrFraction!.Value, f.Return1h, f.Return24h,
                Math.Round(metrics.QuoteVolume24h, 0), Round(metrics.VolumeRatio, 2), f.SpreadFraction!.Value,
                Math.Round(metrics.RoundTripCost, 6, MidpointRounding.AwayFromZero), noEntry));
        }

        var inputsBySymbol = inputs.ToDictionary(i => i.Symbol, StringComparer.Ordinal);
        var exits = held.OrderBy(h => h.Symbol, StringComparer.Ordinal)
            .Select(h => Exit(h, inputsBySymbol.GetValueOrDefault(h.Symbol)?.Features))
            .OfType<ExitSignal>()
            .ToList();

        return new ScanResult(now, ranked, excluded, entries, exits);
    }

    /// <summary>Taker fee both ways, the spread once (buy at the ask, sell at the bid) and slippage both ways.</summary>
    public static decimal RoundTripCost(decimal spreadFraction, ScannerParameters parameters) =>
        (2m * parameters.TakerFeeRate) + spreadFraction + (2m * parameters.SlippageFraction);

    internal sealed record Metrics(decimal QuoteVolume24h, decimal? VolumeRatio, decimal RoundTripCost);

    private static (Metrics? Metrics, ScanExclusion? Exclusion) Evaluate(ScanInput input, ScannerParameters p)
    {
        ScanExclusion Out(string reason, string detail) => new(input.Symbol, reason, detail);
        var f = input.Features;

        if (!input.Allowed)
            return (null, Out("not-allowed", "Not in the risk allowlist (owner-only config/risk-limits.json)."));
        if (input.Pair is null)
            return (null, Out("not-listed", "The exchange doesn't list this pair."));
        if (!input.Pair.Active)
            return (null, Out("inactive", "The exchange has suspended trading in this pair."));
        if (f.IsStale)
            return (null, Out("stale", "Market data is stale."));
        if (f.Price is not > 0 || f.AtrFraction is not > 0 || f.Atr14 is not > 0 || f.SpreadFraction is null || f.Ema20 is null)
            return (null, Out("insufficient-history", "Not enough candles yet for EMA, ATR or spread."));

        var newest = input.Hourly.Count > 0 ? input.Hourly[^1] : null;
        var last24 = newest is null ? [] : input.Hourly.Where(c => c.Start > newest.Start - TimeSpan.FromHours(24)).ToList();
        if (last24.Count < p.MinHourlyCandles24h)
            return (null, Out("insufficient-history", Inv($"Only {last24.Count} hourly candles in the last 24h.")));

        var quoteVolume = last24.Sum(c => c.Volume * c.Close);
        var averageVolume = last24.Average(c => c.Volume);
        decimal? volumeRatio = averageVolume > 0 ? newest!.Volume / averageVolume : null;
        var cost = RoundTripCost(f.SpreadFraction.Value, p);

        if (quoteVolume < p.MinQuoteVolume24h)
            return (null, Out("low-volume", Inv($"24h volume {quoteVolume:0} is below {p.MinQuoteVolume24h:0}.")));
        if (f.SpreadFraction.Value > p.MaxSpreadFraction)
            return (null, Out("wide-spread", Inv($"Spread {f.SpreadFraction.Value:0.####} is above {p.MaxSpreadFraction:0.####}.")));
        if (f.AtrFraction.Value > p.MaxAtrFraction)
            return (null, Out("too-volatile", Inv($"Hourly ATR {f.AtrFraction.Value:0.####} is above {p.MaxAtrFraction:0.####}.")));
        if (f.AtrFraction.Value < cost * p.MinAtrToCostRatio)
            return (null, Out("costs-exceed-volatility", Inv($"Hourly ATR {f.AtrFraction.Value:0.####} is under {p.MinAtrToCostRatio}× the round-trip cost {cost:0.####}.")));

        return (new Metrics(quoteVolume, volumeRatio, cost), null);
    }

    /// <summary>
    /// Risk-adjusted momentum: 24h return in ATRs (clamped), plus the trend direction, plus a bonus for unusual volume, minus
    /// the round-trip cost in ATRs.
    /// </summary>
    internal static decimal Score(FeatureSet f, Metrics m, ScannerParameters p)
    {
        var atr = f.AtrFraction!.Value;
        var momentum = f.Return24h is { } r ? Math.Clamp(r / atr, -MomentumClamp, MomentumClamp) : 0m;
        var trend = f.Trend switch { Trend.Up => 1m, Trend.Down => -1m, _ => 0m };
        var volume = m.VolumeRatio is { } v ? Math.Clamp(v - 1m, 0m, 2m) : 0m;
        return (p.MomentumWeight * momentum) + (p.TrendWeight * trend) + (p.VolumeWeight * volume) - (p.CostWeight * m.RoundTripCost / atr);
    }

    private static (EntrySignal? Signal, string? NoEntryReason) Entry(ScanInput input, Metrics m, int rank, ScannerParameters p)
    {
        var f = input.Features;
        var pair = input.Pair!;
        if (f.Trend != Trend.Up)
            return (null, $"trend {f.Trend.ToString().ToLowerInvariant()}");
        if (f.Ask is not > 0)
            return (null, "no ask");

        var atr = f.Atr14!.Value;
        var aboveEma = atr == 0 ? 0m : (f.Price!.Value - f.Ema20!.Value) / atr;
        if (aboveEma > p.EntryMaxAboveEma20Atr)
            return (null, Inv($"extended: {aboveEma:0.##} ATR above EMA20"));
        if (f.Return1h is not { } return1h || return1h < 0)
            return (null, "last hour falling");

        var entry = f.Ask.Value;
        var r = p.StopAtr * atr;
        var stop = FloorToStep(entry - r, pair.QuoteStep);
        if (stop <= 0)
            return (null, "stop below zero");
        var target = CeilToStep(entry + (p.RewardRisk * r), pair.QuoteStep);

        // Loss per unit if stopped: the stop distance plus round-trip costs on the entry value.
        var lossPerUnit = (entry - stop) + (m.RoundTripCost * entry);
        if (lossPerUnit <= 0)
            return (null, "no stop distance");
        var size = Math.Min(p.RiskPerTradeQuote / lossPerUnit, p.OrderQuote / entry);
        size = FloorToStep(size, pair.BaseStep > 0 ? pair.BaseStep : 0.00000001m);
        var quote = size * entry;
        if (size <= 0 || size < pair.MinOrderSize || quote < pair.MinOrderSizeQuote)
            return (null, "below exchange minimum");

        var reason = Inv($"uptrend pullback {aboveEma:0.##} ATR above EMA20, 1h {return1h:+0.##%;-0.##%;0%}, 24h {(f.Return24h ?? 0m):+0.##%;-0.##%;0%}");
        return (new EntrySignal(
            input.Symbol, rank, entry, size, Math.Round(quote, 2), stop, target, Math.Round(size * lossPerUnit, 2), reason), null);
    }

    private static ExitSignal? Exit(HeldPosition h, FeatureSet? f)
    {
        if (f is null || f.IsStale || f.Bid is not > 0 || h.SellableBaseSize <= 0)
            return null; // no fresh price: nothing to judge (the stale exclusion shows why)

        var bid = f.Bid.Value;
        if (h.StopLoss is { } stop && bid <= stop)
            return new ExitSignal(h.Symbol, h.SellableBaseSize, bid, "stop", Inv($"bid {bid} at or below stop {stop}"));
        if (h.TakeProfit is { } target && bid >= target)
            return new ExitSignal(h.Symbol, h.SellableBaseSize, bid, "target", Inv($"bid {bid} at or above target {target}"));
        if (f.Trend == Trend.Down)
            return new ExitSignal(h.Symbol, h.SellableBaseSize, bid, "trend-down", "EMA20 crossed below EMA50");
        return null;
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static decimal FloorToStep(decimal value, decimal step) =>
        step > 0 ? Math.Floor(value / step) * step : Math.Round(value, 8, MidpointRounding.ToZero);

    private static decimal CeilToStep(decimal value, decimal step) =>
        step > 0 ? Math.Ceiling(value / step) * step : Math.Round(value, 8, MidpointRounding.AwayFromZero);

    private static decimal? Round(decimal? value, int decimals) =>
        value is null ? null : Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero);
}
