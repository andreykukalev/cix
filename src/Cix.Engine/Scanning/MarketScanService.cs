using System.Globalization;
using System.Text.Json;
using Cix.Domain.Exchange;
using Cix.Domain.Scanning;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Strategy;

namespace Cix.Engine.Scanning;

/// <summary>Bound from the "Scanner" section (ADR-0006).</summary>
public sealed class ScannerOptions
{
    public const string SectionName = "Scanner";

    /// <summary>Established, liquid EUR pairs to consider when configuration names none. No automatic discovery.</summary>
    public static readonly IReadOnlyList<string> DefaultUniverse =
        ["BTC-EUR", "ETH-EUR", "SOL-EUR", "LINK-EUR", "AVAX-EUR", "SUI-EUR", "NEAR-EUR"];

    /// <summary>Candidate symbols. Null or empty means <see cref="DefaultUniverse"/> (a default list would be appended to by binding).</summary>
    public List<string>? Universe { get; set; }

    /// <summary>How often the engine scans in the background and journals new signals.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public ScannerParameters Parameters { get; set; } = new();

    public IReadOnlyList<string> EffectiveUniverse =>
        MarketDataPoller.NormaliseSymbols(Universe is { Count: > 0 } ? Universe : DefaultUniverse);

    public void Validate()
    {
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Scanner:Interval must be between 1 minute and 1 hour.");
        if (EffectiveUniverse.Count > 20)
            throw new InvalidOperationException("Scanner:Universe is limited to 20 symbols.");
        Parameters.Validate();
    }
}

/// <summary>
/// What the scanner considers and what can be traded. Every symbol in the owner's risk allowlist is polled and tradable (the
/// safety monitor expects fresh quotes for all of them); the scan covers the curated universe plus the allowlist, and universe
/// symbols outside the allowlist show as not allowed. The exchange's active pairs are checked at every scan.
/// </summary>
public sealed class MarketUniverse
{
    public MarketUniverse(IEnumerable<string> universe, IReadOnlySet<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(allowlist);
        Tradable = MarketDataPoller.NormaliseSymbols(allowlist);
        Candidates = MarketDataPoller.NormaliseSymbols(universe.Concat(Tradable));
    }

    public IReadOnlyList<string> Candidates { get; }

    public IReadOnlyList<string> Tradable { get; }

    public bool IsTradable(string symbol) => Tradable.Contains(symbol, StringComparer.Ordinal);
}

/// <summary>
/// Gathers features, candles, pair rules, open orders and positions, runs the deterministic <see cref="MarketScanner"/>, and
/// journals each new signal once. Reads only: it never places or cancels orders.
/// </summary>
public sealed partial class MarketScanService(
    MarketUniverse universe,
    FeatureService features,
    ICandleStore candles,
    PairCatalog pairs,
    ExecutionService execution,
    KillSwitch killSwitch,
    IJournal journal,
    IStrategySpecStore specs,
    ScannerOptions options,
    TimeProvider time,
    ILogger<MarketScanService> logger)
{
    public const string SignalKind = "scan-signal";
    public const string Actor = "scanner";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _lastSignals = new(StringComparer.Ordinal);

    /// <summary>The most recent scan, or null before the first.</summary>
    public ScanResult? Latest { get; private set; }

    public bool Halted => killSwitch.State.Engaged;

    /// <summary>True when the active spec lets agents call scan_market: the trader then runs on signals, not on a timer.</summary>
    public bool SignalDriven => specs.Active?.Allows("scan_market") ?? false;

    /// <summary>
    /// The scanner settings in force: the active spec's <c>scanner</c> parameters (with its <c>orderQuote</c>), or configuration
    /// when the spec has none. Resolved once per spec, so a bad spec fails every scan loudly instead of trading on defaults.
    /// </summary>
    public ScannerParameters Parameters
    {
        get
        {
            var spec = specs.Active;
            var cached = _parameters;
            if (cached is not null && cached.Value.Label == spec?.Label)
                return cached.Value.Parameters;
            var resolved = FromSpec(spec, options.Parameters);
            _parameters = (spec?.Label, resolved);
            return resolved;
        }
    }

    private (string? Label, ScannerParameters Parameters)? _parameters;

    /// <summary>
    /// A short, stable description of the latest scan's signals (for example <c>buy:SOL-EUR|sell:BTC-EUR:stop</c>), empty when
    /// there are none. The trader loop starts an agent run when it changes to something non-empty.
    /// </summary>
    public string SignalKey => Latest is { } scan ? KeyOf(scan, StaleOrderSymbols) : string.Empty;

    /// <summary>Orders resting longer than this need a run to cancel them (the specs' 50-minute tidy rule).</summary>
    public static readonly TimeSpan StaleOrderAge = TimeSpan.FromMinutes(50);

    /// <summary>
    /// Symbols with an open order older than <see cref="StaleOrderAge"/> at the latest scan. They count as signals, so an exit
    /// hidden behind an unfilled order, or a forgotten buy, gets a run within the hour rather than at the next heartbeat.
    /// </summary>
    public IReadOnlySet<string> StaleOrderSymbols { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public static string KeyOf(ScanResult scan, IEnumerable<string>? staleOrderSymbols = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return string.Join('|', scan.Exits.Select(x => $"sell:{x.Symbol}:{x.Reason}")
            .Concat(scan.Entries.Select(e => $"buy:{e.Symbol}"))
            .Concat((staleOrderSymbols ?? []).Select(s => $"stale-order:{s}"))
            .Order(StringComparer.Ordinal));
    }

    private static readonly JsonSerializerOptions SpecJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, // a typo fails loudly
    };

    /// <summary>Scanner settings from a spec's <c>scanner</c> object and top-level <c>orderQuote</c>; <paramref name="fallback"/> otherwise.</summary>
    public static ScannerParameters FromSpec(StrategySpec? spec, ScannerParameters fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (spec is null)
            return fallback;

        var result = fallback;
        if (spec.Parameters.TryGetValue("scanner", out var scanner))
        {
            if (scanner.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{spec.Label}: parameters.scanner must be an object.");
            if (scanner.TryGetProperty("orderQuote", out _) || scanner.TryGetProperty("OrderQuote", out _))
                throw new InvalidOperationException($"{spec.Label}: set orderQuote once, at the top of parameters, not inside scanner.");
            try
            {
                result = scanner.Deserialize<ScannerParameters>(SpecJson)
                    ?? throw new InvalidOperationException($"{spec.Label}: parameters.scanner is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{spec.Label}: parameters.scanner is not valid ({ex.Message}).", ex);
            }
        }

        // One orderQuote per spec: the scanner's size cap and the order-size hook read the same number.
        if (spec.Parameters.TryGetValue("orderQuote", out var orderQuote))
        {
            if (orderQuote.ValueKind != JsonValueKind.Number || !orderQuote.TryGetDecimal(out var quote))
                throw new InvalidOperationException($"{spec.Label}: parameters.orderQuote must be a number.");
            result = result with { OrderQuote = quote };
        }

        result.Validate();
        return result;
    }

    /// <summary>Scans now and journals signals that weren't in the previous scan.</summary>
    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await RunAsync(cancellationToken).ConfigureAwait(false);
            await RecordNewSignalsAsync(result).ConfigureAwait(false);
            Latest = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScanResult> RunAsync(CancellationToken cancellationToken)
    {
        var plans = await PositionPlans.LoadAsync(execution, journal, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The portfolio can't be valued right now.");
        var open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var withOpenOrder = open.Where(o => o.IsOpen).Select(o => MarketDataPoller.NormaliseSymbol(o.Symbol)).ToHashSet(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        StaleOrderSymbols = open.Where(o => o.IsOpen && now - o.CreatedAt > StaleOrderAge)
            .Select(o => MarketDataPoller.NormaliseSymbol(o.Symbol)).ToHashSet(StringComparer.Ordinal);

        var inputs = new List<ScanInput>();
        foreach (var symbol in universe.Candidates)
        {
            var allowed = universe.IsTradable(symbol);
            PairInfo? pair = allowed ? await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false) : null;
            var hourly = allowed ? candles.Recent(symbol, MarketDataPoller.HourInterval, FeatureService.HourlyCandlesNeeded) : Array.Empty<Candle>();
            inputs.Add(new ScanInput(symbol, features.GetFeatures(symbol), hourly, pair, allowed, withOpenOrder.Contains(symbol)));
        }

        var held = plans.Select(p => new HeldPosition(p.Symbol, p.SellableBaseSize, p.StopLoss, p.TakeProfit)).ToList();
        return MarketScanner.Scan(inputs, held, Parameters, time.GetUtcNow(), allowEntries: !Halted);
    }

    /// <summary>One journal entry per signal when it first appears, so reports can compare signals taken and not taken.</summary>
    private async Task RecordNewSignalsAsync(ScanResult result)
    {
        var current = new Dictionary<string, (string Summary, Dictionary<string, string> Data)>(StringComparer.Ordinal);
        foreach (var e in result.Entries)
        {
            current[$"buy:{e.Symbol}"] = (
                Inv($"buy {e.Symbol} #{e.Rank}: {e.BaseSize} at {e.LimitPrice}, stop {e.StopLoss}, target {e.TakeProfit} ({e.Reason})"),
                Data(e.Symbol, "buy", "entry", e.LimitPrice, e.BaseSize, ("stopLoss", e.StopLoss), ("takeProfit", e.TakeProfit), ("riskQuote", e.RiskQuote), ("rank", e.Rank)));
        }

        foreach (var x in result.Exits)
        {
            current[$"sell:{x.Symbol}:{x.Reason}"] = (
                Inv($"sell {x.Symbol} {x.BaseSize} at {x.LimitPrice}: {x.Reason} ({x.Detail})"),
                Data(x.Symbol, "sell", x.Reason, x.LimitPrice, x.BaseSize));
        }

        var spec = specs.Active?.Label;
        foreach (var (key, (summary, data)) in current.Where(c => !_lastSignals.Contains(c.Key)).ToList())
        {
            try
            {
                await journal.AppendAsync(
                    new JournalEntry(Guid.NewGuid().ToString("N"), result.AsOf, SignalKind, Actor, null, spec, summary, data),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogJournalFailed(logger, key, ex.Message);
                current.Remove(key); // try again at the next scan
            }
        }

        _lastSignals = current.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Data(
        string symbol, string side, string reason, decimal price, decimal size, params (string Key, decimal Value)[] extra)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["symbol"] = symbol,
            ["side"] = side,
            ["reason"] = reason,
            ["limitPrice"] = price.ToString(CultureInfo.InvariantCulture),
            ["baseSize"] = size.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in extra)
            data[key] = value.ToString(CultureInfo.InvariantCulture);
        return data;
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for scan signal {Signal}: {Detail}")]
    private static partial void LogJournalFailed(ILogger logger, string signal, string detail);
}

/// <summary>Scans every <see cref="ScannerOptions.Interval"/>. A failed scan is logged and retried at the next tick.</summary>
public sealed partial class MarketScanBackgroundService(
    MarketScanService scanner,
    ScannerOptions options,
    TimeProvider time,
    ILogger<MarketScanBackgroundService> logger) : BackgroundService
{
    /// <summary>One scan; returns false if it failed. Tests call this instead of running the loop.</summary>
    public async Task<bool> ScanOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            LogScanned(logger, result.Ranked.Count, result.Excluded.Count, result.Entries.Count, result.Exits.Count, Describe(result));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScanFailed(logger, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First scan after a minute (time for quotes and candles to load), then every interval.
        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
        await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>One line an owner can read in the logs: why each symbol did or didn't produce a signal.</summary>
    public static string Describe(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var ranked = result.Ranked.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"#{r.Rank} {r.Symbol} {r.Score:0.##} ({r.NoEntryReason ?? "signal"})"));
        var excluded = result.Excluded.Select(e => $"{e.Symbol} {e.Reason}");
        var exits = result.Exits.Select(x => $"{x.Symbol} {x.Reason}");
        return $"ranked [{string.Join("; ", ranked)}] excluded [{string.Join("; ", excluded)}] exits [{string.Join("; ", exits)}]";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan: {Ranked} ranked, {Excluded} excluded, {Entries} entry and {Exits} exit signals: {Detail}")]
    private static partial void LogScanned(ILogger logger, int ranked, int excluded, int entries, int exits, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan failed ({Error}): {Detail}")]
    private static partial void LogScanFailed(ILogger logger, string error, string detail);
}

public static class ScannerRegistration
{
    /// <summary>Registers the universe (universe ∩ risk allowlist), the scanner and its background loop.</summary>
    public static IServiceCollection AddCixScanner(this IServiceCollection services, ScannerOptions options, MarketUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(universe);
        services.AddSingleton<MarketScanService>();
        services.AddHostedService<MarketScanBackgroundService>();
        return services;
    }
}
