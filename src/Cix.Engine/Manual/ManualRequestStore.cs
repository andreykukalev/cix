using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Engine.Persistence;
using Microsoft.Azure.Cosmos;

namespace Cix.Engine.Manual;

/// <summary>A request document with the version it was read at, so a change can't overwrite one made since.</summary>
public sealed record StoredRequest(JsonObject Document, string ETag)
{
    public string Id => ManualRequestDocument.Id(Document) ?? string.Empty;

    public string Day => ManualRequestDocument.Day(Document) ?? string.Empty;

    public string Status => ManualRequestDocument.Status(Document);
}

/// <summary>Where requests live: the <c>requests</c> container, partitioned by UTC day (docs/persistence.md).</summary>
public interface IManualRequestStore
{
    /// <summary><c>ok</c>, <c>unavailable</c> (the container doesn't exist: run provisioning again) or <c>not-used</c> (no database).</summary>
    string State { get; }

    /// <summary>
    /// At most <see cref="MaxListed"/> requests of <paramref name="type"/> for <paramref name="mode"/> (<c>paper</c> also takes
    /// those without a <c>tradingMode</c>) of the UTC days <paramref name="from"/> to <paramref name="to"/> that are still
    /// <c>new</c> (or have no status). Everything after <c>new</c> is in the engine's own ledger, so later statuses aren't read
    /// back. A document without a <c>type</c> is a manual request.
    /// </summary>
    Task<IReadOnlyList<StoredRequest>> ListNewAsync(DateOnly from, DateOnly to, string mode, CancellationToken cancellationToken, string type = ManualRequestDocument.Type);

    /// <summary>The most new requests one poll reads, so a flood of documents can't use up the database's throughput.</summary>
    public const int MaxListed = 50;

    Task<StoredRequest?> GetAsync(string id, string day, CancellationToken cancellationToken);

    /// <summary>Replaces the document if nobody changed it since it was read. Returns the new version, or null if it had changed.</summary>
    Task<StoredRequest?> TryReplaceAsync(StoredRequest original, JsonObject updated, CancellationToken cancellationToken);
}

/// <summary>Without a database (and for tests): requests can be added in code only.</summary>
public sealed class InMemoryManualRequestStore(string state = "not-used") : IManualRequestStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredRequest> _items = new(StringComparer.Ordinal);
    private int _version;

    public string State { get; set; } = state;

    /// <summary>Makes replaces throw, as a database outage would.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Adds a document as the dashboard would (create only: an existing id is refused).</summary>
    public bool Add(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            var id = ManualRequestDocument.Id(document) ?? string.Empty;
            if (_items.ContainsKey(id))
                return false;
            _items[id] = new StoredRequest((JsonObject)document.DeepClone(), NextETag());
            return true;
        }
    }

    public StoredRequest? Find(string id)
    {
        lock (_gate)
            return _items.GetValueOrDefault(id) is { } item ? item with { Document = (JsonObject)item.Document.DeepClone() } : null;
    }

    /// <summary>Simulates someone else changing the document.</summary>
    public void Touch(string id)
    {
        lock (_gate)
            _items[id] = _items[id] with { ETag = NextETag() };
    }

    public Task<IReadOnlyList<StoredRequest>> ListNewAsync(DateOnly from, DateOnly to, string mode, CancellationToken cancellationToken, string type = ManualRequestDocument.Type) =>
        Task.FromResult<IReadOnlyList<StoredRequest>>(Select(r => string.CompareOrdinal(r.Day, Day(from)) >= 0 && string.CompareOrdinal(r.Day, Day(to)) <= 0
            && r.Status == ManualRequestStatus.New && ManualRequestDocument.ModeOf(r.Document) == mode
            && (ManualRequestDocument.Field(r.Document, "type") ?? ManualRequestDocument.Type) == type).Take(IManualRequestStore.MaxListed).ToList());

    public Task<StoredRequest?> GetAsync(string id, string day, CancellationToken cancellationToken)
    {
        var found = Find(id);
        return Task.FromResult(found is not null && found.Day == day ? found : null);
    }

    public Task<StoredRequest?> TryReplaceAsync(StoredRequest original, JsonObject updated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        if (FailWrites)
            throw new IOException("The request could not be saved.");
        lock (_gate)
        {
            if (!_items.TryGetValue(original.Id, out var current) || current.ETag != original.ETag)
                return Task.FromResult<StoredRequest?>(null);
            var next = new StoredRequest((JsonObject)updated.DeepClone(), NextETag());
            _items[original.Id] = next;
            return Task.FromResult<StoredRequest?>(next with { Document = (JsonObject)next.Document.DeepClone() });
        }
    }

    private IReadOnlyList<StoredRequest> Select(Func<StoredRequest, bool> predicate)
    {
        lock (_gate)
            return _items.Values.Where(predicate).Select(r => r with { Document = (JsonObject)r.Document.DeepClone() }).ToList();
    }

    private string NextETag() => $"v{++_version}";

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The <c>requests</c> container. The engine reads requests and replaces them (with an ETag check) to record what it did;
/// it never creates or deletes one. Unavailable when the container doesn't exist in Azure yet (the engine can't create it
/// there: run tools/azure/provision.ps1), in which case manual requests are off and the rest of the engine runs as before.
/// </summary>
public sealed class CosmosManualRequestStore(CosmosDb db) : IManualRequestStore
{
    /// <summary>Cosmos DB's own fields: not sent back on a replace.</summary>
    private static readonly string[] SystemFields = ["_rid", "_self", "_etag", "_attachments", "_ts", "_lsn"];

    public string State => db.RequestsAvailable ? "ok" : "unavailable";

    public Task<IReadOnlyList<StoredRequest>> ListNewAsync(DateOnly from, DateOnly to, string mode, CancellationToken cancellationToken, string type = ManualRequestDocument.Type)
    {
        // The mode and type are filtered here, not after reading: documents for another mode or kind of request cost nothing.
        var modeFilter = mode == "paper" ? "(NOT IS_DEFINED(c.tradingMode) OR c.tradingMode = @mode)" : "c.tradingMode = @mode";
        var typeFilter = type == ManualRequestDocument.Type ? "(NOT IS_DEFINED(c.type) OR c.type = @type)" : "c.type = @type";
        return QueryAsync(
            new QueryDefinition(
                    $"SELECT TOP {IManualRequestStore.MaxListed} * FROM c WHERE c.day >= @from AND c.day <= @to AND (NOT IS_DEFINED(c.status) OR c.status = @new) AND {modeFilter} AND {typeFilter}")
                .WithParameter("@from", Day(from)).WithParameter("@to", Day(to)).WithParameter("@new", ManualRequestStatus.New).WithParameter("@mode", mode)
                .WithParameter("@type", type),
            cancellationToken);
    }

    public async Task<StoredRequest?> GetAsync(string id, string day, CancellationToken cancellationToken)
    {
        try
        {
            var response = await db.Requests.ReadItemAsync<JsonObject>(id, new PartitionKey(day), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new StoredRequest(response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredRequest?> TryReplaceAsync(StoredRequest original, JsonObject updated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        var body = (JsonObject)updated.DeepClone();
        foreach (var field in SystemFields)
            body.Remove(field);
        try
        {
            var response = await db.Requests.ReplaceItemAsync(
                body, original.Id, new PartitionKey(original.Day), new ItemRequestOptions { IfMatchEtag = original.ETag }, cancellationToken)
                .ConfigureAwait(false);
            return new StoredRequest(body, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Generous for any real request; the engine refuses documents beyond its own, much smaller, limits.</summary>
    private static readonly JsonDocumentOptions PageOptions = new() { MaxDepth = 1_000 };

    /// <summary>
    /// Reads pages as raw JSON, so one odd document (very deep or very large) can't make the whole query fail: each one is
    /// handed to the engine, which refuses it on its own.
    /// </summary>
    private async Task<IReadOnlyList<StoredRequest>> QueryAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        var results = new List<StoredRequest>();
        using var iterator = db.Requests.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { MaxItemCount = 50 });
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var page = await JsonDocument.ParseAsync(response.Content, PageOptions, cancellationToken).ConfigureAwait(false);
            foreach (var element in page.RootElement.GetProperty("Documents").EnumerateArray())
            {
                // A query result carries each document's version in _etag.
                if (element.ValueKind == JsonValueKind.Object && JsonObject.Create(element.Clone()) is { } document
                    && document["_etag"] is JsonValue etag && etag.GetValueKind() == JsonValueKind.String)
                    results.Add(new StoredRequest(document, etag.GetValue<string>()));
            }
        }

        return results;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
