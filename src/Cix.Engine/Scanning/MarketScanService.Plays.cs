using System.Globalization;
using System.Text.Json;
using Cix.Domain.Exchange;
using Cix.Domain.Scanning;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Strategy;

namespace Cix.Engine.Scanning;

/// <summary>The plays beside a strategy's own entries (ADR-0031): bursts and BTC events, judged on Binance's candles.</summary>
public sealed partial class MarketScanService
{
    public const string PlaysKey = "plays";

    /// <summary>Days of the journal's fills the plays' loss stop adds up (the longest hold is 30 days).</summary>
    public const int PlayLossDays = 60;

    private static readonly JsonSerializerOptions PlaysJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The spec's <c>plays</c>, checked; null when it has none. A malformed one throws, like a bad <c>scanner</c>.</summary>
    public static PlayParameters? PlaysOf(StrategySpec? spec)
    {
        if (spec?.Parameters is not { } p || !p.TryGetValue(PlaysKey, out var plays))
            return null;
        if (plays.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{spec.Label}: parameters.plays must be an object.");
        PlayParameters parsed;
        try
        {
            parsed = plays.Deserialize<PlayParameters>(PlaysJson) ?? throw new InvalidOperationException($"{spec.Label}: parameters.plays is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{spec.Label}: parameters.plays is not valid ({ex.Message}).", ex);
        }

        parsed.Validate();
        return parsed.Any ? parsed : null;
    }

    /// <summary>Whether the spec runs plays; a malformed <c>plays</c> counts as none here (the scan reports it).</summary>
    public static bool HasPlays(StrategySpec? spec)
    {
        try
        {
            return PlaysOf(spec) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The play signal a buy of <paramref name="symbol"/> takes (ADR-0031): the latest scan's, if it is from the last
    /// <paramref name="maxAge"/>. Null when there is none. Throws when the buy doesn't keep to it: a play buy carries the
    /// signal's stop or a higher one, and at most its size, since the engine then sells it only at that stop or its time.
    /// </summary>
    public EntrySignal? PlaySignalFor(string symbol, decimal baseSize, decimal? stopLoss, TimeSpan maxAge)
    {
        if (Latest is not { } scan || time.GetUtcNow() - scan.AsOf > maxAge
            || scan.Entries.FirstOrDefault(e => e.Play is not null && string.Equals(e.Symbol, symbol, StringComparison.Ordinal)) is not { } signal)
            return null;
        if (stopLoss is not { } stop || stop < signal.StopLoss || baseSize > signal.BaseSize)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{symbol} has a {signal.Play} play signal: buy at most its baseSize {signal.BaseSize} with its stopLoss {signal.StopLoss} (or a higher stop)."));
        return signal;
    }

    /// <summary>
    /// With plays, the strategy's own entries stop once the coins it bought itself (not the plays') reach
    /// <see cref="PlayParameters.MainExposureFraction"/> of the capital, so the plays keep their room.
    /// </summary>
    internal string? MainExposurePaused(PlayParameters? plays)
    {
        if (plays is null || execution.LatestSample?.Snapshot is not { } snapshot)
            return null;
        var capital = riskLimits?.CapitalOf(snapshot.Equity) ?? Math.Max(0m, snapshot.Equity);
        var states = execution.PositionPlanStates;
        var own = snapshot.PositionQuoteBySymbol.Where(p => states.GetValueOrDefault(p.Key)?.Play is null).Sum(p => p.Value)
            + (snapshot.OpenBuyQuoteBySymbol ?? new Dictionary<string, decimal>()).Where(p => states.GetValueOrDefault(p.Key)?.Play is null).Sum(p => p.Value);
        return own >= plays.MainExposureFraction * capital ? "strategy's own exposure cap reached (the rest is the plays')" : null;
    }

    private async Task<ScanResult> WithPlaysAsync(
        ScanResult result, PlayParameters plays, IReadOnlyList<ScanInput> inputs, IReadOnlyList<HeldPosition> held, ScannerParameters costs,
        DateTimeOffset now, string? paused, CancellationToken cancellationToken)
    {
        var capital = execution.LatestSample?.Snapshot is { } snapshot ? riskLimits?.CapitalOf(snapshot.Equity) ?? Math.Max(0m, snapshot.Equity) : (decimal?)null;
        var reason = Halted ? "trading halted"
            : paused ?? (views?.Current?.BlocksEntries(now) == true ? "risk-off (market view)" : null)
            ?? (capital is not > 0 ? "capital not valued yet" : null)
            ?? (binance is null ? "no Binance data in this engine" : null)
            ?? await PlayLossStopAsync(plays, capital!.Value, now, cancellationToken).ConfigureAwait(false);

        var hourly = binance is null
            ? new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal)
            : inputs.Where(i => i.Allowed).ToDictionary(i => i.Symbol, i => binance.Hourly(i.Symbol), StringComparer.Ordinal);
        // A play's buy still on the book counts as held for the plays' own limits (the most bursts, one BTC event at a time).
        var pending = execution.PositionPlanStates.Values
            .Where(x => x.Play is not null && x.Status == PositionPlanState.EntryPending && held.All(h => h.Symbol != x.Symbol))
            .Select(x => new HeldPosition(x.Symbol, 0m, x.StopLoss, x.TakeProfit, x.Since, x.Play, x.ExitBy));
        held = [.. held, .. pending];
        var analyst = views?.Current;
        // A coin the analyst flagged isn't bought by a play either.
        var usable = inputs.Where(i => analyst?.FlagFor(i.Symbol, now) is null).ToList();
        PlayResult outcome;
        try
        {
            outcome = MarketPlays.Evaluate(usable, hourly, binance?.BtcDaily() ?? [], held, plays, capital ?? 0m, costs, now, reason);
        }
#pragma warning disable CA1031 // Binance's figures are only signals: if they can't be judged the plays wait, and the swing and every exit go on
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogPlaysFailed(logger, ex.GetType().Name, ex.Message);
            var why = $"plays couldn't be judged ({ex.GetType().Name})";
            outcome = new PlayResult([], new[] { (MarketPlays.Rally, plays.Rally), (MarketPlays.Crash, plays.Crash) }
                .Where(x => x.Item2 is not null).Select(x => new PlayStatus(x.Item1, "paused", why))
                .Concat(plays.Burst is null ? [] : [new PlayStatus(MarketPlays.Burst, "paused", why)]).ToList());
        }


        // The strategy's own signal for a coin wins over a play's, and every signal keeps a distinct rank.
        var entries = result.Entries.ToList();
        foreach (var entry in outcome.Entries.Where(e => entries.All(x => x.Symbol != e.Symbol)))
            entries.Add(entry with { Rank = entries.Count + 1 });
        return result with { Entries = entries, Plays = outcome.Statuses };
    }

    /// <summary>
    /// Why the plays may not buy: together they have lost <see cref="PlayParameters.LossStopFraction"/> of the capital on the
    /// plays bought in the last <see cref="PlayLossDays"/> days: their sells less their buys and fees, and the value of what is
    /// still held of them. A sell is matched to the play buys of its coin first in, first out, so a sale of a coin bought
    /// before the window counts neither as a gain nor as a loss. Null when they haven't; a journal that can't be read stops them too.
    /// </summary>
    private async Task<string?> PlayLossStopAsync(PlayParameters plays, decimal capital, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<JournalEntry> fills;
        try
        {
            // Back far enough to see the buy of every play still held or sold in the window (holds are at most MarketPlays.MaxHold).
            fills = await journal.QueryAsync(
                new JournalQuery(From: DateOnly.FromDateTime(now.UtcDateTime).AddDays(-PlayLossDays - (int)MarketPlays.MaxHold.TotalDays - 1), Kind: "fill", Limit: FileJournal.MaxQueryLimit),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // any journal failure: the plays wait, everything else goes on
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogRecentSellsFailed(logger, ex.Message);
            return "plays' results unknown (journal unavailable)";
        }

        if (fills.Count >= FileJournal.MaxQueryLimit)
            return "plays' results unknown (too many fills to add up)";

        var windowStart = now - TimeSpan.FromDays(PlayLossDays);
        var quoteCurrency = riskLimits?.AllowedQuoteCurrency;
        var lots = new Dictionary<string, List<(decimal Size, bool InWindow)>>(StringComparer.Ordinal);
        var net = 0m;
        foreach (var fill in fills.Where(f => f.Data?.GetValueOrDefault("play") is { Length: > 0 } && f.Data.GetValueOrDefault("symbol") is { Length: > 0 }).OrderBy(f => f.At))
        {
            var data = fill.Data!;
            var symbol = data["symbol"];
            var size = Number(data.GetValueOrDefault("filledSize"));
            var quote = Number(data.GetValueOrDefault("filledQuote"));
            var fee = Number(data.GetValueOrDefault("fee"));
            // A fee taken in the coin is already out of the size held and sold; one in the quote currency is money spent.
            var feeInCoin = data.GetValueOrDefault("feeCurrency") is { Length: > 0 } currency
                && !string.Equals(currency, quoteCurrency ?? currency, StringComparison.OrdinalIgnoreCase);
            var quoteFee = feeInCoin ? 0m : fee;
            var coinLots = lots.TryGetValue(symbol, out var l) ? l : lots[symbol] = [];
            if (data.GetValueOrDefault("side") == "buy")
            {
                var inWindow = fill.At >= windowStart;
                coinLots.Add((Math.Max(0m, size - (feeInCoin ? fee : 0m)), inWindow));
                if (inWindow)
                    net -= quote + quoteFee;
            }
            else if (data.GetValueOrDefault("side") == "sell" && size > 0)
            {
                var left = size;
                var counted = 0m;
                while (left > 0 && coinLots.Count > 0)
                {
                    var (lotSize, inWindow) = coinLots[0];
                    var taken = Math.Min(lotSize, left);
                    if (inWindow)
                        counted += taken;
                    left -= taken;
                    if (taken >= lotSize)
                        coinLots.RemoveAt(0);
                    else
                        coinLots[0] = (lotSize - taken, inWindow);
                }

                // Only the share sold of coins bought in the window; a sale with no recorded buy counts for nothing.
                net += (quote - quoteFee) * (counted / size);
            }
        }

        // What is still held of the window's play buys, at its value now.
        var states = execution.PositionPlanStates;
        if (execution.LatestSample?.Snapshot is { } snapshot)
        {
            foreach (var (symbol, value) in snapshot.PositionQuoteBySymbol.Where(p => states.GetValueOrDefault(p.Key)?.Play is not null))
            {
                var remaining = lots.GetValueOrDefault(symbol) ?? [];
                var total = remaining.Sum(x => x.Size);
                if (total > 0)
                    net += value * (remaining.Where(x => x.InWindow).Sum(x => x.Size) / total);
            }
        }

        var stop = plays.LossStopFraction * capital;
        return net <= -stop
            ? string.Create(CultureInfo.InvariantCulture, $"plays' loss stop: {-net:0.##} lost, the stop is {plays.LossStopFraction:0.#%} of the capital ({stop:0.##})")
            : null;
    }

    private static decimal Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plays not judged this scan ({Error}): {Detail}; the strategy's own scan and the exits go on")]
    private static partial void LogPlaysFailed(ILogger logger, string error, string detail);
}
