namespace Cix.Engine.Manual;

/// <summary>A request the engine accepted and still acts on, with what it knows about it.</summary>
/// <param name="Request">The request as the engine read and checked it when it first saw it.</param>
/// <param name="Status"><c>awaiting-agent</c>, <c>executing</c>, <c>placed</c> or <c>unconfirmed</c>.</param>
/// <param name="Since">When it reached <paramref name="Status"/> (the engine's clock).</param>
/// <param name="ClientOrderId">The order placed for it, from the moment it was claimed.</param>
public sealed record LedgerEntry(
    ManualRequest Request,
    string Status,
    DateTimeOffset Since,
    string? ClientOrderId = null,
    DateTimeOffset? ClaimedAt = null,
    string? DecidedBy = null,
    string? RunId = null);

/// <summary>
/// The engine's own record of manual requests (ADR-0011): the requests it accepted and still acts on, and today's counts. It
/// lives in the engine's <c>state</c> container, which the dashboard can't write, so only the engine moves a request beyond
/// <c>new</c>: the status, outcome and history on a request document are a copy for people to read, never trusted back.
/// </summary>
/// <param name="Accepted">Requests accepted on <paramref name="Day"/>; the daily cap counts these.</param>
/// <param name="Refused">Requests refused on <paramref name="Day"/>; past a limit they are no longer journaled one by one.</param>
/// <param name="Seen">
/// Every request accepted since yesterday, as <c>day/id</c>: such a request is never taken as new again, even if its document
/// still says <c>new</c> because a write to it failed.
/// </param>
public sealed record ManualLedger(
    IReadOnlyDictionary<string, LedgerEntry> Active, DateOnly? Day = null, int Accepted = 0, int Refused = 0, IReadOnlyList<string>? Seen = null)
{
    public static readonly ManualLedger Empty = new(new Dictionary<string, LedgerEntry>(StringComparer.Ordinal));

    public bool HasSeen(string day, string id) => (Seen ?? []).Contains(day + "/" + id, StringComparer.Ordinal);

    public ManualLedger WithSeen(string day, string id) => this with { Seen = [.. Seen ?? [], day + "/" + id] };

    /// <summary>A new UTC day: counts restart, and only requests from yesterday on stay in <see cref="Seen"/>.</summary>
    public ManualLedger ForDay(DateOnly day)
    {
        var from = day.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return this with
        {
            Day = day,
            Accepted = 0,
            Refused = 0,
            Seen = (Seen ?? []).Where(s => string.CompareOrdinal(s, from) >= 0).ToList(),
        };
    }

    public ManualLedger With(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return this with { Active = new Dictionary<string, LedgerEntry>(Active, StringComparer.Ordinal) { [entry.Request.Id] = entry } };
    }

    public ManualLedger Without(string id)
    {
        var next = new Dictionary<string, LedgerEntry>(Active, StringComparer.Ordinal);
        next.Remove(id);
        return this with { Active = next };
    }
}

public interface IManualLedgerStore
{
    Task<ManualLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ManualLedger ledger, CancellationToken cancellationToken);
}

/// <summary>Without a database (and for tests). <see cref="Fail"/> makes saves throw.</summary>
public sealed class InMemoryManualLedgerStore : IManualLedgerStore
{
    public ManualLedger? Saved { get; set; }

    public bool Fail { get; set; }

    public Task<ManualLedger?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);

    public Task SaveAsync(ManualLedger ledger, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new IOException("The ledger could not be saved.");
        Saved = ledger;
        return Task.CompletedTask;
    }
}
