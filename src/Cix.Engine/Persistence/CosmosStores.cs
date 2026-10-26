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

        // Day, kind, run and client order ID filter in the database; the spec prefix after reading.
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

        if (query.ClientOrderId is not null)
        {
            sql += " AND c.entry.data.clientOrderId = @clientOrderId";
            parameters.Add(("@clientOrderId", query.ClientOrderId));
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

/// <summary>The engine's status (heartbeat, last sync, portfolio) as <c>engine-status-&lt;mode&gt;</c>, replaced every minute.</summary>
public sealed class CosmosEngineStatusStore(CosmosDb db, TradingMode mode) : Status.IEngineStatusStore
{
    public const string DocumentPrefix = "engine-status";
    public const string About =
        "The engine's status, rewritten every minute: heartbeat, last sync with the exchange, last scan, market view and portfolio. For people and the dashboard; the engine never reads it back.";

    public bool UsesDatabase => true;

    public Task SaveAsync(Status.EngineStatus status, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, $"{DocumentPrefix}-{mode.ToString().ToLowerInvariant()}", status, DocumentPrefix, About, cancellationToken);
}

/// <summary>The coin filters in force and which coins pass, as <c>scanner-filters-&lt;mode&gt;</c>; replaced only when they change.</summary>
public sealed class CosmosScannerFilterStore(CosmosDb db, TradingMode mode) : Scanning.IScannerFilterStore
{
    public const string DocumentPrefix = "scanner-filters";
    public const string About =
        "The coin filters in force (from the active strategy spec), since when, and which coins passed them at the last change. Replaced only when the filters or the set of passing coins change; the journal's scanner-filters and scan-eligibility entries keep the history.";

    private string Id => $"{DocumentPrefix}-{mode.ToString().ToLowerInvariant()}";

    public Task<Scanning.ScannerFilterState?> LoadAsync(CancellationToken cancellationToken) =>
        CosmosDb.ReadStateAsync<Scanning.ScannerFilterState>(db.State, Id, cancellationToken);

    public Task SaveAsync(Scanning.ScannerFilterState state, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, Id, state, DocumentPrefix, About, cancellationToken);
}

/// <summary>
/// The engine's own record of manual requests, as <c>manual-requests-&lt;mode&gt;</c> (ADR-0011): the requests it accepted and
/// still acts on, with each claim's client order ID, and today's counts. Saved before a request's order is sent.
/// </summary>
public sealed class CosmosManualLedgerStore(CosmosDb db, TradingMode mode) : Manual.IManualLedgerStore
{
    public const string DocumentPrefix = "manual-requests";
    public const string About =
        "The engine's own record of the owner's manual requests: those it accepted and still acts on (waiting for the trader, being sent, or placed), with each order's client order ID, and today's counts. The request documents in the requests container carry a copy of each step for people; the engine trusts only this record.";

    private string Id => $"{DocumentPrefix}-{mode.ToString().ToLowerInvariant()}";

    public Task<Manual.ManualLedger?> LoadAsync(CancellationToken cancellationToken) =>
        CosmosDb.ReadStateAsync<Manual.ManualLedger>(db.State, Id, cancellationToken);

    public Task SaveAsync(Manual.ManualLedger ledger, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, Id, ledger, DocumentPrefix, About, cancellationToken);
}

/// <summary>
/// The agent-run scheduler's state, as <c>agent-runs-&lt;mode&gt;</c> (ADR-0013): today's run counts, the signal and request keys
/// last handled, the next heartbeat and analyst slot, and the runs queued and not yet ended.
/// </summary>
public sealed class CosmosAgentRunStateStore(CosmosDb db, TradingMode mode) : AgentRuns.IAgentRunStateStore
{
    public const string DocumentPrefix = "agent-runs";
    public const string About =
        "When the engine queues agent runs: today's counts against the daily caps, the signals and owner requests last handled, the next heartbeat and analyst slot, and the runs queued that haven't ended. Saved before each run is queued, only if unchanged since the engine read it; the journal's agent-run entries keep the history.";

    private string Id => $"{DocumentPrefix}-{mode.ToString().ToLowerInvariant()}";

    public async Task<AgentRuns.StoredAgentRunState?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await db.State.ReadItemAsync<CosmosDb.StateDocument<AgentRuns.AgentRunState>>(Id, new PartitionKey(Id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AgentRuns.StoredAgentRunState(response.Resource.Value, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string?> SaveAsync(AgentRuns.AgentRunState state, string? etag, CancellationToken cancellationToken)
    {
        var document = new CosmosDb.StateDocument<AgentRuns.AgentRunState>(Id, state, DocumentPrefix, About, DateTimeOffset.UtcNow);
        try
        {
            var response = etag is null
                ? await db.State.CreateItemAsync(document, new PartitionKey(Id), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await db.State.ReplaceItemAsync(document, Id, new PartitionKey(Id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null; // another engine wrote it since this one read it
        }
    }
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

/// <summary>The engine lease for one trading mode: <c>engine-lease-paper</c> (or <c>-live</c>) in <c>state</c>.</summary>
public sealed class CosmosLeaseStore(CosmosDb db, TradingMode mode) : ILeaseStore
{
    public const string DocumentPrefix = "engine-lease";
    public const string About =
        "Which engine may trade in this mode. The holder renews it every 15 seconds; another engine takes it only after it was released or stayed unchanged for 2 minutes. Holder null means released.";

    public string Id => $"{DocumentPrefix}-{mode.ToString().ToLowerInvariant()}";

    public async Task<StoredLease?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await db.State.ReadItemAsync<CosmosDb.StateDocument<LeaseRecord>>(Id, new PartitionKey(Id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new StoredLease(response.Resource.Value, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string?> TryCreateAsync(LeaseRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var response = await db.State.CreateItemAsync(Document(record), new PartitionKey(Id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    public async Task<string?> TryReplaceAsync(LeaseRecord record, string etag, CancellationToken cancellationToken)
    {
        try
        {
            var response = await db.State.ReplaceItemAsync(Document(record), Id, new PartitionKey(Id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private CosmosDb.StateDocument<LeaseRecord> Document(LeaseRecord record) => new(Id, record, DocumentPrefix, About, record.UpdatedAt);
}
