using System.Net;
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

    /// <summary>Requests from <paramref name="since"/> on whose status the engine still acts on (<see cref="ManualRequestStatus.Active"/>).</summary>
    Task<IReadOnlyList<StoredRequest>> ListActiveAsync(DateOnly since, CancellationToken cancellationToken);

    /// <summary>Every request of one UTC day.</summary>
    Task<IReadOnlyList<StoredRequest>> ListDayAsync(DateOnly day, CancellationToken cancellationToken);

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

    public Task<IReadOnlyList<StoredRequest>> ListActiveAsync(DateOnly since, CancellationToken cancellationToken) =>
        Task.FromResult(Select(r => string.CompareOrdinal(r.Day, Day(since)) >= 0 && ManualRequestStatus.Active.Contains(r.Status)));

    public Task<IReadOnlyList<StoredRequest>> ListDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        Task.FromResult(Select(r => r.Day == Day(day)));

    public Task<StoredRequest?> GetAsync(string id, string day, CancellationToken cancellationToken)
    {
        var found = Find(id);
        return Task.FromResult(found is not null && found.Day == day ? found : null);
    }

    public Task<StoredRequest?> TryReplaceAsync(StoredRequest original, JsonObject updated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
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

    public Task<IReadOnlyList<StoredRequest>> ListActiveAsync(DateOnly since, CancellationToken cancellationToken)
    {
        var statuses = string.Join(", ", ManualRequestStatus.Active.Order(StringComparer.Ordinal).Select((_, i) => $"@s{i}"));
        var query = new QueryDefinition($"SELECT * FROM c WHERE c.day >= @since AND (NOT IS_DEFINED(c.status) OR c.status IN ({statuses}))")
            .WithParameter("@since", Day(since));
        var i = 0;
        foreach (var status in ManualRequestStatus.Active.Order(StringComparer.Ordinal))
            query = query.WithParameter($"@s{i++}", status);
        return QueryAsync(query, null, cancellationToken);
    }

    public Task<IReadOnlyList<StoredRequest>> ListDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        QueryAsync(new QueryDefinition("SELECT * FROM c"), Day(day), cancellationToken);

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

    private async Task<IReadOnlyList<StoredRequest>> QueryAsync(QueryDefinition query, string? partition, CancellationToken cancellationToken)
    {
        var options = partition is null ? null : new QueryRequestOptions { PartitionKey = new PartitionKey(partition) };
        var results = new List<StoredRequest>();
        using var iterator = db.Requests.GetItemQueryIterator<JsonObject>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                // A query result carries each document's version in _etag.
                var etag = document["_etag"]?.GetValue<string>();
                if (etag is not null)
                    results.Add(new StoredRequest(document, etag));
            }
        }

        return results;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
