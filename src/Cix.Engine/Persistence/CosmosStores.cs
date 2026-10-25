using System.Net;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Paper;
using Cix.Engine.Strategy;
using Microsoft.Azure.Cosmos;

namespace Cix.Engine.Persistence;

/// <summary>
/// The journal in Cosmos DB: one document per entry, partitioned by UTC day. Append-only: this class creates documents
/// and never replaces, patches or deletes one (CLAUDE.md rule 4).
/// </summary>
public sealed class CosmosJournal(CosmosDb db) : IJournal
{
    internal sealed record JournalDocument(string Id, string Day, JournalEntry Entry);

    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var day = Day(DateOnly.FromDateTime(entry.At.UtcDateTime));
        try
        {
            await db.Journal.CreateItemAsync(new JournalDocument(entry.Id, day, entry), new PartitionKey(day), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not IOException)
        {
            // Callers treat IOException as "the journal couldn't be written"; timeouts and network errors included.
            throw new IOException($"Journal write failed ({ex.GetType().Name}).", ex);
        }
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, FileJournal.MaxQueryLimit);

        // Day, kind and run filter in the database; the data-field filters (client order ID, spec) after reading.
        var sql = "SELECT * FROM c WHERE 1 = 1";
        var parameters = new List<(string, object)>();
        if (query.From is { } from)
        {
            sql += " AND c.day >= @from";
            parameters.Add(("@from", Day(from)));
        }

        if (query.To is { } to)
        {
            sql += " AND c.day <= @to";
            parameters.Add(("@to", Day(to)));
        }

        if (query.Kind is not null)
        {
            sql += " AND c.entry.kind = @kind";
            parameters.Add(("@kind", query.Kind));
        }

        if (query.RunId is not null)
        {
            sql += " AND c.entry.runId = @runId";
            parameters.Add(("@runId", query.RunId));
        }

        var definition = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
            definition = definition.WithParameter(name, value);

        var entries = new List<JournalEntry>();
        using var iterator = db.Journal.GetItemQueryIterator<JournalDocument>(definition);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (FileJournal.Matches(document.Entry, query))
                    entries.Add(document.Entry);
            }
        }

        // Sorted here rather than with ORDER BY, which the emulator doesn't run across partitions in parallel.
        return entries.OrderByDescending(e => e.At).ThenByDescending(e => e.Id, StringComparer.Ordinal).Take(limit).ToList();
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Engine state (kill switch, loss baselines, known orders, holdings baseline) in one Cosmos DB document. The interface is
/// synchronous because callers update it under their own locks; writes are rare (state changes only). A missing document
/// starts with the kill switch engaged in Live mode, as with the state file.
/// </summary>
public sealed class CosmosEngineStateStore : IEngineStateStore
{
    public const string DocumentId = "engine-state";
    public const string About =
        "The engine's operational state: kill switch, loss baselines, holdings at the last reconciliation, orders sent and not yet finished, and position plans (stop and target by symbol).";

    private readonly Container _container;
    private readonly TimeProvider _time;
    private readonly string _id;
    private readonly Lock _gate = new();
    private string? _etag;

    public CosmosEngineStateStore(CosmosDb db, TradingMode mode, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _container = db.State;
        _time = time;
        _id = $"{DocumentId}-{mode.ToString().ToLowerInvariant()}";
        var (saved, etag) = Read();
        if (saved is not null && saved.KillSwitch is null)
            throw new InvalidOperationException("The engine state document has no kill switch state; fix or remove it deliberately.");

        _etag = etag;
        Current = saved ?? (mode == TradingMode.Live
            ? EngineState.Initial with { KillSwitch = new KillSwitchState(true, FileEngineStateStore.NewFileReason, "engine", time.GetUtcNow()) }
            : EngineState.Initial);
        if (saved is null)
            Write(Current);
    }

    public EngineState Current { get; private set; }

    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Current);
            if (next == Current)
                return Current;

            // Engaging takes effect in memory first, so a database outage never leaves trading running.
            if (next.KillSwitch.Engaged && !Current.KillSwitch.Engaged)
                Current = next;

            Write(next);
            Current = next;
            return Current;
        }
    }

    private (EngineState? State, string? ETag) Read()
    {
        try
        {
            var response = _container.ReadItemAsync<CosmosDb.StateDocument<EngineState>>(_id, new PartitionKey(_id)).GetAwaiter().GetResult();
            return (response.Resource.Value, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Creates the document, or replaces it only if nobody else changed it since this store last read or wrote it, so a
    /// second engine instance (or a stray edit) can't silently overwrite an engaged kill switch. Any failure surfaces as
    /// IOException, which callers already handle.
    /// </summary>
    private void Write(EngineState state)
    {
        var document = new CosmosDb.StateDocument<EngineState>(_id, state, DocumentId, About, _time.GetUtcNow());
        try
        {
            var response = _etag is null
                ? _container.CreateItemAsync(document, new PartitionKey(_id)).GetAwaiter().GetResult()
                : _container.ReplaceItemAsync(document, _id, new PartitionKey(_id), new ItemRequestOptions { IfMatchEtag = _etag }).GetAwaiter().GetResult();
            _etag = response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new IOException("Engine state was changed by something else; restart the engine to reload it.", ex);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // Includes SDK timeouts (an OperationCanceledException): no caller token is involved here.
            throw new IOException($"Engine state could not be saved ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>Where the paper account is saved between restarts.</summary>
public interface IPaperAccountStore
{
    Task<PaperAccountState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(PaperAccountState state, CancellationToken cancellationToken);
}

public sealed class CosmosPaperAccountStore(CosmosDb db) : IPaperAccountStore
{
    public const string DocumentId = "paper-account";
    public const string About =
        "The simulated Revolut X account for paper trading: balances (available and reserved) and its orders (open ones and the latest finished).";

    public Task<PaperAccountState?> LoadAsync(CancellationToken cancellationToken) =>
        CosmosDb.ReadStateAsync<PaperAccountState>(db.State, DocumentId, cancellationToken);

    public Task SaveAsync(PaperAccountState state, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, DocumentId, state, DocumentId, About, cancellationToken);
}

/// <summary>For tests.</summary>
public sealed class InMemoryPaperAccountStore : IPaperAccountStore
{
    public PaperAccountState? Saved { get; private set; }

    public int Saves { get; private set; }

    public Task<PaperAccountState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);

    public Task SaveAsync(PaperAccountState state, CancellationToken cancellationToken)
    {
        Saved = state;
        Saves++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps an immutable copy of every strategy spec version that runs, keyed by <c>name@vN#hash</c>, so the journal's spec
/// label always resolves to the exact instructions and parameters, even if the file changes later.
/// </summary>
public sealed class CosmosSpecArchive(CosmosDb db)
{
    public async Task<bool> ArchiveAsync(StrategySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var id = SpecId(spec.Label);
        try
        {
            var document = new CosmosDb.StateDocument<ArchivedSpec>(
                id, new ArchivedSpec(spec.Label, spec.Hash, spec), "strategy-spec",
                "An immutable copy of a strategy spec version that ran; journal entries name it in specVersion.", DateTimeOffset.UtcNow);
            await db.State.CreateItemAsync(document, new PartitionKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false; // already archived: versions are never overwritten
        }
    }

    public async Task<StrategySpec?> GetAsync(string label, CancellationToken cancellationToken)
    {
        var archived = await CosmosDb.ReadStateAsync<ArchivedSpec>(db.State, SpecId(label), cancellationToken).ConfigureAwait(false);
        return archived is null ? null : archived.Spec with { Hash = archived.Hash };
    }

    /// <summary>The hash isn't part of the spec's JSON, so it is stored beside it.</summary>
    internal sealed record ArchivedSpec(string Label, string Hash, StrategySpec Spec);

    /// <summary>Cosmos DB ids can't contain <c>/ \ ? #</c>: <c>trend@v1#abc</c> becomes <c>spec-trend@v1-abc</c>.</summary>
    internal static string SpecId(string label) => "spec-" + label.Replace('#', '-').Replace('/', '-').Replace('\\', '-').Replace('?', '-');
}
