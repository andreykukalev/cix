namespace Cix.Domain.Risk;

/// <summary>
/// The pairs that can be traded now: <see cref="RiskLimits.AllowedSymbols"/> at start, then the list the owner sets from the
/// dashboard at run time. Every list must be one the limits permit (<see cref="Problems"/>), so the owner can narrow or widen
/// trading only within <c>config/risk-limits.json</c>. The risk gate reads it on every order; one read is one consistent list.
/// </summary>
public sealed class TradableSymbols
{
    private Snapshot _current;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    public TradableSymbols(RiskLimits limits, DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
        var at = since ?? DateTimeOffset.MinValue;
        _current = new Snapshot(
            limits.AllowedSymbols.ToHashSet(StringComparer.Ordinal),
            limits.AllowedSymbols.ToDictionary(s => s, _ => at, StringComparer.Ordinal));
    }

    public RiskLimits Limits { get; }

    /// <summary>The pairs tradable now.</summary>
    public IReadOnlySet<string> Current => Volatile.Read(ref _current).Symbols;

    public bool Contains(string symbol) => Current.Contains(symbol);

    /// <summary>How many times the list has changed since start.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Completes once the list has changed since <paramref name="seen"/> (a <see cref="Version"/>): at once if it already has,
    /// so a change made while the caller was busy isn't missed. The scanner rescans on it, so a new pair shows without waiting.
    /// </summary>
    public Task WaitForChangeAsync(long seen, CancellationToken cancellationToken)
    {
        var changed = Volatile.Read(ref _changed); // before the version: a change in between shows in the version
        return Version != seen ? Task.CompletedTask : changed.Task.WaitAsync(cancellationToken);
    }

    /// <summary>When <paramref name="symbol"/> became tradable (<see cref="DateTimeOffset.MinValue"/> for those there from the start), or null.</summary>
    public DateTimeOffset? AddedAt(string symbol) =>
        Volatile.Read(ref _current).AddedAt.TryGetValue(symbol, out var at) ? at : null;

    /// <summary>Why <paramref name="symbols"/> can't be the tradable list; empty when it can.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var problems = new List<string>();
        if (symbols.Count == 0)
            problems.Add("At least one pair must stay tradable.");
        if (symbols.Count > Limits.TradableCap)
            problems.Add($"At most {Limits.TradableCap} pairs can be tradable at once (config/risk-limits.json).");
        var refused = symbols.Where(s => !Limits.Permits(s)).Order(StringComparer.Ordinal).ToList();
        if (refused.Count > 0)
            problems.Add($"Not permitted by config/risk-limits.json: {string.Join(", ", refused.Take(5))}.");
        return problems;
    }

    /// <summary>
    /// Makes <paramref name="symbols"/> the tradable list, if the limits permit it. Pairs already tradable keep their
    /// <see cref="AddedAt"/>; new ones get <paramref name="at"/>. Returns the problems, empty when applied.
    /// </summary>
    public IReadOnlyList<string> TrySet(IEnumerable<string> symbols, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var set = symbols.ToHashSet(StringComparer.Ordinal);
        var problems = Problems(set);
        if (problems.Count > 0)
            return problems;
        var previous = Volatile.Read(ref _current);
        var addedAt = set.ToDictionary(s => s, s => previous.AddedAt.TryGetValue(s, out var since) ? since : at, StringComparer.Ordinal);
        Volatile.Write(ref _current, new Snapshot(set, addedAt));
        if (!previous.Symbols.SetEquals(set))
        {
            Interlocked.Increment(ref _version);
            Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        return problems;
    }

    private sealed record Snapshot(IReadOnlySet<string> Symbols, IReadOnlyDictionary<string, DateTimeOffset> AddedAt);
}
