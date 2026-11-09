using System.Globalization;
using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Domain.Scanning;

/// <summary>
/// The volume burst (ADR-0031): an hourly close above the highest high of the <see cref="BreakoutHours"/> before it, on at
/// least <see cref="VolumeMultiple"/> times the average hourly volume of the <see cref="VolumeHours"/> before it, while BTC's
/// daily close is above its EMA of <see cref="RegimeEmaPeriod"/> days. All three on Binance's USDT candles.
/// </summary>
public sealed record BurstPlay
{
    public decimal OrderFraction { get; init; } = 0.06m;
    public int MaxPositions { get; init; } = 4;
    public int BreakoutHours { get; init; } = 96;
    public int VolumeHours { get; init; } = 168;
    public decimal VolumeMultiple { get; init; } = 4m;
    public decimal StopAtr { get; init; } = 4m;
    public int HoldHours { get; init; } = 72;
    public int RegimeEmaPeriod { get; init; } = 100;
}

/// <summary>
/// A sharp BTC move (ADR-0031): on the first daily close where BTC's move over <see cref="MoveDays"/> days reaches
/// <see cref="BtcMoveFraction"/> (above it for a rally, a positive fraction; below it for a crash, a negative one), buy every
/// coin of <see cref="Basket"/> and hold it <see cref="HoldDays"/> days behind a stop <see cref="StopFraction"/> below the buy.
/// </summary>
public sealed record EventPlay
{
    public decimal OrderFraction { get; init; } = 0.06m;
    public decimal BtcMoveFraction { get; init; }
    public int MoveDays { get; init; } = 3;
    public decimal StopFraction { get; init; }
    public int HoldDays { get; init; }
    public IReadOnlyList<string> Basket { get; init; } = [];
}

/// <summary>The plays a strategy runs beside its own entries, and their shared loss stop.</summary>
/// <param name="LossStopFraction">No new play once the plays together have lost this share of the capital (closed and open).</param>
/// <param name="MainExposureFraction">The most the strategy's own entries (not the plays') may hold, as a share of the capital.</param>
public sealed record PlayParameters
{
    public BurstPlay? Burst { get; init; }
    public EventPlay? Rally { get; init; }
    public EventPlay? Crash { get; init; }
    public decimal LossStopFraction { get; init; } = 0.18m;
    public decimal MainExposureFraction { get; init; } = 0.4m;

    public bool Any => Burst is not null || Rally is not null || Crash is not null;

    public void Validate()
    {
        if (LossStopFraction is <= 0 or > 0.5m || MainExposureFraction is < 0 or > 1)
            throw new InvalidOperationException("plays.lossStopFraction must be above 0 and at most 0.5, and plays.mainExposureFraction 0 to 1.");
        if (Burst is { } b && (b.OrderFraction is <= 0 or > 0.25m || b.MaxPositions is < 1 or > 10 || b.BreakoutHours is < 12 or > 240
            || b.VolumeHours is < 24 or > 240 || b.VolumeMultiple is < 1 or > 20 || b.StopAtr is < 1 or > 10 || b.HoldHours is < 1 or > 720
            || b.RegimeEmaPeriod is < 0 or 1 or > 150))
            throw new InvalidOperationException("plays.burst is out of range: orderFraction (0, 0.25], maxPositions 1-10, breakoutHours 12-240, volumeHours 24-240, volumeMultiple 1-20, stopAtr 1-10, holdHours 1-720 (30 days), regimeEmaPeriod 0 or 2-150.");
        foreach (var (e, name, sign) in new[] { (Rally, "rally", 1), (Crash, "crash", -1) })
        {
            if (e is null)
                continue;
            if (e.OrderFraction is <= 0 or > 0.25m || e.BtcMoveFraction * sign is < 0.03m or > 0.5m || e.MoveDays is < 1 or > 10
                || e.StopFraction is < 0.02m or > 0.5m || e.HoldDays is < 1 or > 30 || e.Basket is null || e.Basket.Count is < 1 or > 10
                || e.Basket.Any(c => string.IsNullOrWhiteSpace(c)))
                throw new InvalidOperationException($"plays.{name} is out of range: orderFraction (0, 0.25], btcMoveFraction {(sign > 0 ? "0.03 to 0.5" : "-0.5 to -0.03")}, moveDays 1-10, stopFraction 0.02-0.5, holdDays 1-30, a basket of 1 to 10 coins.");
        }
    }
}

/// <summary>Where a play stands at this scan, for the trader and the owner: <c>waiting</c>, <c>signal</c>, <c>full</c> or <c>paused</c>.</summary>
public sealed record PlayStatus(string Play, string State, string Detail);

/// <summary>A play's buy signals and how each play stands.</summary>
public sealed record PlayResult(IReadOnlyList<EntrySignal> Entries, IReadOnlyList<PlayStatus> Statuses);

/// <summary>
/// The plays of ADR-0031, judged on Binance's candles and bought at Revolut X's ask: a volume burst in one coin, and a
/// basket bought after a sharp BTC rally or crash. Each buy has a stop and a time to sell by, and no trend exit or trail.
/// Pure arithmetic, like <see cref="MarketScanner"/>.
/// </summary>
public static class MarketPlays
{
    public const string Burst = "burst";
    public const string Rally = "rally";
    public const string Crash = "crash";

    /// <summary>The longest a play may hold: the loss stop looks this far back for the buys it matches sells to.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromDays(30);

    /// <summary>
    /// Binance's USDT close over Revolut X's EUR mid must be within this range, or the two tickers aren't the same coin (or one
    /// price is wrong) and the burst isn't bought. EUR has been worth 1.0 to 1.25 USD for years.
    /// </summary>
    public const decimal MinUsdtPerEur = 0.8m;

    public const decimal MaxUsdtPerEur = 1.5m;

    /// <summary>A Binance hourly candle that closed longer ago than this is too old for a burst.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private const int AtrPeriod = 14;

    /// <param name="inputs">The watched coins, as the scan sees them on Revolut X (price, pair, open orders, the last sell).</param>
    /// <param name="binanceHourly">Closed Binance hourly candles per watched coin, oldest first; a coin missing has no burst.</param>
    /// <param name="btcDaily">Closed Binance daily BTC candles, oldest first.</param>
    /// <param name="capital">The capital the order sizes are shares of.</param>
    /// <param name="pausedReason">Why no play may buy now (halted, the loss stop, a risk-off view), or null.</param>
    public static PlayResult Evaluate(
        IReadOnlyList<ScanInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> binanceHourly,
        IReadOnlyList<Candle> btcDaily,
        IReadOnlyList<HeldPosition> held,
        PlayParameters p,
        decimal capital,
        ScannerParameters costs,
        DateTimeOffset now,
        string? pausedReason)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(binanceHourly);
        ArgumentNullException.ThrowIfNull(btcDaily);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(costs);
        var entries = new List<EntrySignal>();
        var statuses = new List<PlayStatus>();
        var heldSymbols = held.Select(h => h.Symbol).ToHashSet(StringComparer.Ordinal);
        var btcFresh = btcDaily.Count > 0 && now - (btcDaily[^1].Start + Day) <= MarketScanner.MaxDailyCandleAge;

        foreach (var (play, e) in new[] { (Rally, p.Rally), (Crash, p.Crash) })
        {
            if (e is null)
                continue;
            if (pausedReason is not null)
            {
                statuses.Add(new PlayStatus(play, "paused", pausedReason));
                continue;
            }

            var (trigger, detail) = EventTrigger(play, e, btcDaily, btcFresh, now);
            if (trigger is not { } triggerClose)
            {
                statuses.Add(new PlayStatus(play, "waiting", detail));
                continue;
            }

            // One event at a time: positions of an earlier event block a new one; this event's own first buys don't.
            if (held.Any(h => h.Play is Rally or Crash && !(h.Play == play && h.FilledAt >= triggerClose)))
            {
                statuses.Add(new PlayStatus(play, "full", Inv($"{detail}; positions of an earlier BTC event are still held, so this one waits.")));
                continue;
            }

            var bought = 0;
            foreach (var symbol in e.Basket)
            {
                if (heldSymbols.Contains(symbol) || entries.Any(x => x.Symbol == symbol))
                    continue;
                var input = inputs.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.Ordinal));
                if (input is null || Unbuyable(input, triggerClose, costs) is not null)
                    continue;
                var ask = input.Features.Ask!.Value;
                if (Sized(input, play, ask, ask * (1 - e.StopFraction), e.OrderFraction, capital, costs, now + TimeSpan.FromDays(e.HoldDays),
                    Inv($"BTC {play}: {detail}; hold {e.HoldDays} days, stop {e.StopFraction:0.#%} below")) is { } signal)
                {
                    entries.Add(signal with { Rank = entries.Count + 1 });
                    bought++;
                }
            }

            statuses.Add(new PlayStatus(play, bought > 0 ? "signal" : "waiting", bought > 0 ? detail : Inv($"{detail}; no basket coin can be bought now (held, an open order, sold since, a wide spread or no price).")));
        }

        if (p.Burst is { } b)
        {
            if (pausedReason is not null)
                statuses.Add(new PlayStatus(Burst, "paused", pausedReason));
            else
                statuses.Add(Bursts(inputs, binanceHourly, btcDaily, btcFresh, held, heldSymbols, b, capital, costs, now, entries));
        }

        return new PlayResult(entries, statuses);
    }

    /// <summary>The close of the BTC daily candle that started an event and a sentence on it; null with the reason when there is none to buy.</summary>
    private static (DateTimeOffset? TriggerClose, string Detail) EventTrigger(string play, EventPlay e, IReadOnlyList<Candle> btc, bool fresh, DateTimeOffset now)
    {
        if (!fresh || btc.Count < e.MoveDays + 2)
            return (null, "BTC's daily candles from Binance are missing or behind.");
        var closes = btc.Select(c => c.Close).ToList();
        var move = Move(closes, closes.Count - 1, e.MoveDays);
        var before = Move(closes, closes.Count - 2, e.MoveDays);
        var rally = e.BtcMoveFraction > 0;
        bool Reached(decimal m) => rally ? m >= e.BtcMoveFraction : m <= e.BtcMoveFraction;
        var text = Inv($"BTC {move:+0.#%;-0.#%;0%} over {e.MoveDays} days to the last daily close (the {play} play starts at {e.BtcMoveFraction:+0.#%;-0.#%})");
        if (!Reached(move))
            return (null, text);
        if (Reached(before))
            return (null, Inv($"{text}; it was already there a day earlier, so this move's buys were due then."));
        var close = btc[^1].Start + Day;
        return now < close + Day ? (close, text) : (null, Inv($"{text}; the day to buy on has passed."));
    }

    private static decimal Move(IReadOnlyList<decimal> closes, int at, int days) =>
        at - days >= 0 && closes[at - days] > 0 ? (closes[at] / closes[at - days]) - 1 : 0m;

    private static PlayStatus Bursts(
        IReadOnlyList<ScanInput> inputs, IReadOnlyDictionary<string, IReadOnlyList<Candle>> binanceHourly, IReadOnlyList<Candle> btcDaily, bool btcFresh,
        IReadOnlyList<HeldPosition> held, HashSet<string> heldSymbols, BurstPlay b, decimal capital, ScannerParameters costs, DateTimeOffset now,
        List<EntrySignal> entries)
    {
        if (b.RegimeEmaPeriod > 0)
        {
            var ema = Indicators.Ema(btcDaily.Select(c => c.Close).ToList(), b.RegimeEmaPeriod);
            if (!btcFresh || ema is not > 0)
                return new PlayStatus(Burst, "waiting", Inv($"Bursts wait for BTC's daily trend on Binance (its close against its EMA{b.RegimeEmaPeriod}), and those candles are missing or behind."));
            if (btcDaily[^1].Close <= ema)
                return new PlayStatus(Burst, "waiting", Inv($"BTC's last daily close is {(ema.Value - btcDaily[^1].Close) / ema.Value:0.##%} below its EMA{b.RegimeEmaPeriod}; bursts are bought only while BTC is above it."));
        }

        var open = held.Count(h => h.Play == Burst);
        if (open >= b.MaxPositions)
            return new PlayStatus(Burst, "full", Inv($"{open} burst positions held, the most the play allows."));

        var found = new List<(ScanInput Input, decimal Multiple, decimal AtrFraction, DateTimeOffset Close)>();
        foreach (var input in inputs.OrderBy(i => i.Symbol, StringComparer.Ordinal))
        {
            if (heldSymbols.Contains(input.Symbol) || entries.Any(x => x.Symbol == input.Symbol)
                || binanceHourly.GetValueOrDefault(input.Symbol) is not { } hourly || hourly.Count < Math.Max(b.BreakoutHours, b.VolumeHours) + 1)
                continue;
            var last = hourly[^1];
            var close = last.Start + Hour;
            if (now - close > BurstWindow || now < close)
                continue; // the burst is the hour that just closed
            var priorHigh = hourly.Skip(hourly.Count - 1 - b.BreakoutHours).Take(b.BreakoutHours).Max(c => c.High);
            var averageVolume = hourly.Skip(hourly.Count - 1 - b.VolumeHours).Take(b.VolumeHours).Average(c => c.Volume);
            if (last.Close <= priorHigh || averageVolume <= 0 || last.Volume < b.VolumeMultiple * averageVolume)
                continue;
            var atr = Indicators.Atr(hourly, AtrPeriod);
            if (atr is not > 0 || last.Close <= 0 || Unbuyable(input, last.Start, costs) is not null)
                continue;
            if (input.Features.Price is not > 0 || last.Close / input.Features.Price.Value is < MinUsdtPerEur or > MaxUsdtPerEur)
                continue; // Binance's coin under this ticker isn't Revolut X's, or a price is wrong
            found.Add((input, last.Volume / averageVolume, atr.Value / last.Close, close));
        }

        var added = 0;
        foreach (var (input, multiple, atrFraction, close) in found.OrderByDescending(f => f.Multiple).ThenBy(f => f.Input.Symbol, StringComparer.Ordinal))
        {
            if (open + added >= b.MaxPositions)
                break;
            var ask = input.Features.Ask!.Value;
            if (Sized(input, Burst, ask, ask * (1 - (b.StopAtr * atrFraction)), b.OrderFraction, capital, costs, now + TimeSpan.FromHours(b.HoldHours),
                Inv($"volume burst: the hour to {close.UtcDateTime:HH:mm} UTC closed above the {b.BreakoutHours}-hour high on {multiple:0.#}x the average volume (Binance); hold {b.HoldHours} h, stop {b.StopAtr:0.#} hourly ATR below")) is { } signal)
            {
                entries.Add(signal with { Rank = entries.Count + 1 });
                added++;
            }
        }

        return added > 0
            ? new PlayStatus(Burst, "signal", Inv($"{added} burst(s) this hour."))
            : new PlayStatus(Burst, "waiting", Inv($"No coin's last hour closed above its {b.BreakoutHours}-hour high on {b.VolumeMultiple:0.#}x its average volume (Binance)."));
    }

    /// <summary>Why a coin can't be bought by a play now, or null: not tradable, no fresh ask, a spread wider than the strategy allows, an open order, or sold since <paramref name="since"/>.</summary>
    private static string? Unbuyable(ScanInput input, DateTimeOffset since, ScannerParameters costs)
    {
        if (!input.Allowed || input.Pair is not { Active: true } || input.Features.IsStale || input.Features.Ask is not > 0)
            return "unpriced";
        if (input.Features.SpreadFraction is not { } spread || spread > costs.MaxSpreadFraction)
            return "wide spread"; // the strategy's widest spread holds for the plays too
        if (input.HasOpenOrder)
            return "open order";
        return input.LastSoldAt is { } sold && sold >= since ? "sold since" : null;
    }

    private static EntrySignal? Sized(
        ScanInput input, string play, decimal ask, decimal rawStop, decimal orderFraction, decimal capital, ScannerParameters costs, DateTimeOffset exitBy, string reason)
    {
        var pair = input.Pair!;
        var stop = pair.QuoteStep > 0 ? Math.Floor(rawStop / pair.QuoteStep) * pair.QuoteStep : Math.Round(rawStop, 8, MidpointRounding.ToZero);
        if (stop <= 0 || stop > ask * (1 - MarketScanner.MinExitDistanceFraction))
            return null;
        var quote = Math.Round(orderFraction * Math.Max(0m, capital), 2, MidpointRounding.ToZero);
        var step = pair.BaseStep > 0 ? pair.BaseStep : 0.00000001m;
        var size = Math.Floor(quote / ask / step) * step;
        if (size <= 0 || size < pair.MinOrderSize || size * ask < pair.MinOrderSizeQuote)
            return null;
        var spread = input.Features.SpreadFraction ?? 0m;
        var lossPerUnit = (ask - stop) + (MarketScanner.RoundTripCost(spread, costs) * ask);
        return new EntrySignal(input.Symbol, 0, ask, size, Math.Round(size * ask, 2), stop, null, Math.Round(size * lossPerUnit, 2), reason, play, exitBy);
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
