using System.Text.Json;
using Cix.Domain.Scanning;
using Cix.Engine.Journal;

namespace Cix.Engine.Scanning;

/// <summary>
/// The coin filters in force and which coins pass them, as last recorded: the <c>scanner-filters-&lt;mode&gt;</c> document in
/// the state container. Filters come from the active strategy spec (a change is a new spec version); this records what was
/// applied and when, so the record survives restarts and the dashboard can show it.
/// </summary>
/// <param name="Spec">The spec label the filters came from.</param>
/// <param name="Since">When these filters were first applied.</param>
/// <param name="Passing">Coins that passed every filter, sorted.</param>
/// <param name="Excluded">Coins that didn't, with the scanner's reason code (for example <c>low-volume</c>).</param>
/// <param name="EligibilityAt">The scan that set <paramref name="Passing"/> and <paramref name="Excluded"/>.</param>
/// <param name="LastChange">The last time the filters changed (a strategy switch), with the coins it moved; the Coins tab shows it.</param>
public sealed record ScannerFilterState(
    string? Spec,
    ScannerParameters Filters,
    DateTimeOffset Since,
    IReadOnlyList<string> Passing,
    IReadOnlyDictionary<string, string> Excluded,
    DateTimeOffset EligibilityAt,
    FilterChange? LastChange = null);

/// <summary>
/// What a change of filters (a strategy switch) did to the watched coins (ADR-0021): those that now pass and didn't, those that
/// passed and don't, and of those that left, the ones the account holds. A switch to a strategy with the same coin filters
/// moves no coin and records no change. Those keep their stops and targets; the
/// owner decides what to do with them.
/// </summary>
public sealed record FilterChange(
    DateTimeOffset At,
    string? FromSpec,
    string? ToSpec,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> HeldOutside);

public interface IScannerFilterStore
{
    Task<ScannerFilterState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ScannerFilterState state, CancellationToken cancellationToken);
}

/// <summary>Without a database (and for tests).</summary>
public sealed class InMemoryScannerFilterStore : IScannerFilterStore
{
    public ScannerFilterState? Saved { get; set; }

    public int Saves { get; private set; }

    public Task<ScannerFilterState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);

    public Task SaveAsync(ScannerFilterState state, CancellationToken cancellationToken)
    {
        Saved = state;
        Saves++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records the coin filters and their effect, incrementally: one state document replaced only when something changed, and
/// append-only journal entries. Nothing else is rewritten or reset.
/// <list type="bullet">
/// <item><c>scanner-filters</c>: the filters in force differ from the recorded ones (or none were recorded yet), with each
/// changed value and the coins that pass under the new filters.</item>
/// <item><c>scan-eligibility</c>: the set of coins that pass changed under the same filters, seen in two scans in a row so a
/// coin hovering at a threshold doesn't flood the journal.</item>
/// </list>
/// A coin excluded only for missing data (<see cref="DataReasons"/>) keeps its last known result: a restart, while candles
/// load, doesn't count as a change.
/// </summary>
public sealed partial class ScannerFilterRecorder(IScannerFilterStore store, IJournal journal, TimeProvider time, ILogger<ScannerFilterRecorder> logger)
{
    public const string FiltersKind = "scanner-filters";
    public const string EligibilityKind = "scan-eligibility";

    /// <summary>Exclusions that mean "no data yet", not "failed a filter".</summary>
    public static readonly IReadOnlySet<string> DataReasons = new HashSet<string>(StringComparer.Ordinal) { "stale", "insufficient-history" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScannerFilterState? _state;
    private bool _loaded;
    private string? _candidate;
    private bool _unsaved;

    /// <summary>The last recorded state, or null before the first scan after startup.</summary>
    public ScannerFilterState? Current => Volatile.Read(ref _state);

    /// <summary>Compares <paramref name="scan"/>, made with <paramref name="filters"/>, against the record and records any change.</summary>
    /// <param name="held">The coins the account holds, so a filter change says which of them no longer pass.</param>
    public async Task RecordAsync(
        ScanResult scan, ScannerParameters filters, string? spec, CancellationToken cancellationToken, IReadOnlyCollection<string>? held = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(filters);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                Volatile.Write(ref _state, await store.LoadAsync(cancellationToken).ConfigureAwait(false));
                _loaded = true;
            }

            if (_unsaved)
                await SaveAsync(_state!, cancellationToken).ConfigureAwait(false); // an earlier change that couldn't be saved

            var previous = _state;
            var (passing, excluded) = Eligibility(scan, previous);

            if (previous is null || previous.Filters != filters)
            {
                var removed = previous?.Passing.Except(passing, StringComparer.Ordinal).ToList() ?? [];
                var change = previous is null ? null : new FilterChange(
                    scan.AsOf, previous.Spec, spec,
                    passing.Except(previous.Passing, StringComparer.Ordinal).ToList(),
                    removed,
                    // Only coins the switch moved out: a balance the strategy never traded (dust, an adopted coin) isn't news.
                    removed.Intersect(held ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
                var next = new ScannerFilterState(spec, filters, scan.AsOf, passing, excluded, scan.AsOf, change ?? previous?.LastChange);
                _candidate = null;
                Take(next); // the change is taken before it is journaled: a failed save never journals it twice
                var data = FiltersData(previous, next);
                if (change is not null)
                {
                    data["added"] = string.Join(',', change.Added);
                    data["removed"] = string.Join(',', change.Removed);
                    data["heldOutside"] = string.Join(',', change.HeldOutside);
                }

                await AppendAsync(FiltersKind, FiltersSummary(previous, next), data, spec, cancellationToken).ConfigureAwait(false);
                await SaveAsync(next, cancellationToken).ConfigureAwait(false);
                return;
            }

            var key = string.Join(',', passing);
            if (key == string.Join(',', previous.Passing))
            {
                _candidate = null;
                if (previous.Spec != spec)
                    await SaveAsync(previous with { Spec = spec }, cancellationToken).ConfigureAwait(false); // same filters, new spec version
                return;
            }

            if (_candidate != key)
            {
                _candidate = key; // first sighting: confirm at the next scan
                return;
            }

            var changed = previous with { Spec = spec, Passing = passing, Excluded = excluded, EligibilityAt = scan.AsOf };
            var added = passing.Except(previous.Passing, StringComparer.Ordinal).ToList();
            var removed = previous.Passing.Except(passing, StringComparer.Ordinal).ToList();
            var summary = $"Coins passing the filters changed: {string.Join(", ", added.Select(s => "+" + s).Concat(removed.Select(s => $"-{s} ({excluded.GetValueOrDefault(s, "?")})")))}"
                + $"; {passing.Count} of {passing.Count + excluded.Count} pass.";
            var data = EligibilityData(changed);
            data["added"] = string.Join(',', added);
            data["removed"] = string.Join(',', removed);
            _candidate = null;
            Take(changed);
            await AppendAsync(EligibilityKind, summary, data, spec, cancellationToken).ConfigureAwait(false);
            await SaveAsync(changed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (IReadOnlyList<string> Passing, IReadOnlyDictionary<string, string> Excluded) Eligibility(ScanResult scan, ScannerFilterState? previous)
    {
        var passing = scan.Ranked.Select(r => r.Symbol).ToHashSet(StringComparer.Ordinal);
        var excluded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in scan.Excluded)
        {
            if (!DataReasons.Contains(x.Reason))
                excluded[x.Symbol] = x.Reason;
            else if (previous?.Passing.Contains(x.Symbol, StringComparer.Ordinal) == true)
                passing.Add(x.Symbol);
            else
                excluded[x.Symbol] = previous?.Excluded.GetValueOrDefault(x.Symbol) ?? x.Reason;
        }

        return (passing.Order(StringComparer.Ordinal).ToList(), excluded);
    }

    private static string FiltersSummary(ScannerFilterState? previous, ScannerFilterState next)
    {
        var outcome = $"{next.Passing.Count} of {next.Passing.Count + next.Excluded.Count} coins pass";
        if (previous is null)
            return $"Coin filters recorded ({next.Spec ?? "configuration"}): {outcome}.";
        var changes = Changes(previous.Filters, next.Filters);
        return $"Coin filters changed ({previous.Spec ?? "configuration"} to {next.Spec ?? "configuration"}): {string.Join("; ", changes)}. Now {outcome}.";
    }

    private static Dictionary<string, string> FiltersData(ScannerFilterState? previous, ScannerFilterState next)
    {
        var data = EligibilityData(next);
        data["filters"] = JsonSerializer.Serialize(next.Filters, Json);
        if (previous is not null)
        {
            data["previousSpec"] = previous.Spec ?? string.Empty;
            data["previousFilters"] = JsonSerializer.Serialize(previous.Filters, Json);
            data["changes"] = string.Join("; ", Changes(previous.Filters, next.Filters));
        }

        return data;
    }

    private static Dictionary<string, string> EligibilityData(ScannerFilterState state) => new(StringComparer.Ordinal)
    {
        ["spec"] = state.Spec ?? string.Empty,
        ["passing"] = string.Join(',', state.Passing),
        ["excluded"] = string.Join(',', state.Excluded.Select(x => $"{x.Key}:{x.Value}")),
    };

    /// <summary>Each setting that differs, as <c>name old → new</c> with camelCase names, in a stable order.</summary>
    public static IReadOnlyList<string> Changes(ScannerParameters before, ScannerParameters after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = JsonSerializer.SerializeToElement(before, Json);
        var now = JsonSerializer.SerializeToElement(after, Json);
        return now.EnumerateObject()
            .Where(p => !old.TryGetProperty(p.Name, out var o) || !JsonElement.DeepEquals(o, p.Value))
            .Select(p => $"{p.Name} {(old.TryGetProperty(p.Name, out var o) ? o.GetRawText() : "unset")} → {p.Value.GetRawText()}") // JSON text: true, not True
            .ToList();
    }

    private async Task AppendAsync(string kind, string summary, Dictionary<string, string> data, string? spec, CancellationToken cancellationToken)
    {
        var at = time.GetUtcNow();
        await journal.AppendAsync(new JournalEntry(JournalIds.New(at, kind), at, kind, MarketScanService.Actor, null, spec, summary, data), cancellationToken)
            .ConfigureAwait(false);
        LogRecorded(logger, kind, summary);
    }

    /// <summary>
    /// Takes the new state at once (so a failed save can't journal the same change again at every scan), then saves it; a
    /// failed save is retried at the next scan.
    /// </summary>
    private void Take(ScannerFilterState state)
    {
        Volatile.Write(ref _state, state);
        _unsaved = true;
    }

    private async Task SaveAsync(ScannerFilterState state, CancellationToken cancellationToken)
    {
        Take(state);
        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        _unsaved = false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind}: {Summary}")]
    private static partial void LogRecorded(ILogger logger, string kind, string summary);
}
