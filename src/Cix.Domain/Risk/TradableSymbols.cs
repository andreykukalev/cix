namespace Cix.Domain.Risk;

/// <summary>
/// The watched pairs (ADR-0021): the list the owner sets from the dashboard at run time, plus the coins the account holds, which
/// the engine adopts. Every list must be one the limits permit (<see cref="Problems"/>): pairs in the owner's quote currency, at
/// most <see cref="RiskLimits.MaxTradableSymbols"/>. It may be empty. The risk gate reads it on every order; one read is one
/// consistent list.
/// </summary>
public sealed class TradableSymbols
{
    private Snapshot _current;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    /// <param name="limits">The owner's limits, which every list must fit.</param>
    /// <param name="initial">The list to start with (tests, mostly); the engine starts empty and restores the saved list.</param>
    /// <param name="since">When the initial pairs count as added.</param>
    public TradableSymbols(RiskLimits limits, IEnumerable<string>? initial = null, DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
        var at = since ?? DateTimeOffset.MinValue;
        var symbols = (initial ?? []).ToHashSet(StringComparer.Ordinal);
        if (Problems(symbols) is { Count: > 0 } problems)
            throw new ArgumentException(string.Join(" ", problems), nameof(initial));
        _current = new Snapshot(symbols, symbols.ToDictionary(s => s, _ => at, StringComparer.Ordinal));
    }

    public RiskLimits Limits { get; }

    /// <summary>No pairs: what a service without the watched list sees, so it never falls back to a wider list.</summary>
    public static IReadOnlySet<string> None { get; } = new HashSet<string>(StringComparer.Ordinal);

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
        if (symbols.Count > Limits.TradableCap)
            problems.Add($"At most {Limits.TradableCap} pairs can be tradable at once (the owner's limits file).");
        var refused = symbols.Where(s => !Limits.Permits(s)).Order(StringComparer.Ordinal).ToList();
        if (refused.Count > 0)
            problems.Add($"Not permitted by the owner's limits file: {string.Join(", ", refused.Take(5))}."); // paper's or live's
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
