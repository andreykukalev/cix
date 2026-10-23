using System.Globalization;
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
/// The curated universe and what can actually be traded: universe symbols in the risk allowlist. Market data is polled for
/// <see cref="Tradable"/> only; the exchange's active pairs are checked at every scan.
/// </summary>
public sealed class MarketUniverse
{
    public MarketUniverse(IEnumerable<string> universe, IReadOnlySet<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(allowlist);
        Candidates = MarketDataPoller.NormaliseSymbols(universe);
        var allowed = MarketDataPoller.NormaliseSymbols(allowlist).ToHashSet(StringComparer.Ordinal);
        Tradable = Candidates.Where(allowed.Contains).ToList();
        if (Tradable.Count == 0)
            throw new InvalidOperationException(
                $"No universe symbol is in the risk allowlist. Universe: {string.Join(", ", Candidates)}; allowed: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
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

        var inputs = new List<ScanInput>();
        foreach (var symbol in universe.Candidates)
        {
            var allowed = universe.IsTradable(symbol);
            PairInfo? pair = allowed ? await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false) : null;
            var hourly = allowed ? candles.Recent(symbol, MarketDataPoller.HourInterval, FeatureService.HourlyCandlesNeeded) : Array.Empty<Candle>();
            inputs.Add(new ScanInput(symbol, features.GetFeatures(symbol), hourly, pair, allowed, withOpenOrder.Contains(symbol)));
        }

        var held = plans.Select(p => new HeldPosition(p.Symbol, p.SellableBaseSize, p.StopLoss, p.TakeProfit)).ToList();
        return MarketScanner.Scan(inputs, held, options.Parameters, time.GetUtcNow(), allowEntries: !Halted);
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
            LogScanned(logger, result.Ranked.Count, result.Excluded.Count, result.Entries.Count, result.Exits.Count);
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
        using var timer = new PeriodicTimer(options.Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan: {Ranked} ranked, {Excluded} excluded, {Entries} entry and {Exits} exit signals")]
    private static partial void LogScanned(ILogger logger, int ranked, int excluded, int entries, int exits);

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
