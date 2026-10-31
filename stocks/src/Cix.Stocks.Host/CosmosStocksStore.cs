using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace Cix.Stocks.Host;

/// <summary>
/// The stocks host's own Cosmos DB account (serverless, in its own resource group), never the crypto engines'. Every
/// container is partitioned by <c>/id</c> and every document is <c>{ id, value }</c>, like the engine's state. The host signs
/// in with its managed identity; provisioning creates the database and containers, as key-based metadata writes are off.
/// </summary>
public sealed class CosmosStocksStore : IStocksStore, IDisposable
{
    public const string DefaultDatabase = "cix-stocks";
    public const string AdviceContainer = "advice";
    public const string StateContainer = "state";
    public const string TradesContainer = "trades";
    public const string BarsContainer = "bars";
    private const string StateId = "paper";
    private const string SearchStateId = "search";
    private const string UniverseId = "universe";

    private readonly CosmosClient _client;
    private readonly string _database;

    public CosmosStocksStore(Uri endpoint, string database, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The stocks database is reached over https only.", nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        _database = database;
        _client = new CosmosClient(endpoint.AbsoluteUri, credential, new CosmosClientOptions
        {
            ApplicationName = "cix-stocks",
            ConnectionMode = ConnectionMode.Gateway, // HTTPS on 443 only
            RequestTimeout = TimeSpan.FromSeconds(30),
            UseSystemTextJsonSerializerWithOptions = StocksJson.Options,
        });
    }

    /// <summary>The store signed in as the user-assigned managed identity with this client id.</summary>
    public static CosmosStocksStore WithManagedIdentity(Uri endpoint, string database, string clientId) =>
        new(endpoint, database, new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId)));

    private Container Get(string name) => _client.GetContainer(_database, name);

    public async Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) =>
        (await ReadAsync<CachedBars>(Get(BarsContainer), symbol, cancellationToken).ConfigureAwait(false))?.Value;

    public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken) =>
        Get(BarsContainer).UpsertItemAsync(new Envelope<CachedBars>(bars.Symbol, bars), new PartitionKey(bars.Symbol), cancellationToken: cancellationToken);

    public async Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken) =>
        await ReadAsync<PaperState>(Get(StateContainer), StateId, cancellationToken).ConfigureAwait(false);

    public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) =>
        SaveVersionedAsync(StateId, state, etag, "Another run saved the paper portfolio first: this run's result was dropped.", cancellationToken);

    public async Task<Versioned<SearchState>?> GetSearchStateAsync(CancellationToken cancellationToken) =>
        await ReadAsync<SearchState>(Get(StateContainer), SearchStateId, cancellationToken).ConfigureAwait(false);

    public Task<string> SaveSearchStateAsync(SearchState state, string? etag, CancellationToken cancellationToken) =>
        SaveVersionedAsync(SearchStateId, state, etag, "Another run saved the search's state first: this search was dropped.", cancellationToken);

    public async Task<StoredUniverse?> GetUniverseAsync(CancellationToken cancellationToken) =>
        (await ReadAsync<StoredUniverse>(Get(StateContainer), UniverseId, cancellationToken).ConfigureAwait(false))?.Value;

    // The state container allows create and replace, not upsert: replace, or create the first time.
    public async Task SaveUniverseAsync(StoredUniverse universe, CancellationToken cancellationToken)
    {
        var container = Get(StateContainer);
        var item = new Envelope<StoredUniverse>(UniverseId, universe);
        try
        {
            await container.ReplaceItemAsync(item, UniverseId, new PartitionKey(UniverseId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await container.CreateItemAsync(item, new PartitionKey(UniverseId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public Task AddCandidatesAsync(string id, CandidatesDocument candidates, CancellationToken cancellationToken) =>
        CreateOnceAsync(Get(AdviceContainer), new Envelope<CandidatesDocument>(id, candidates), cancellationToken);

    public Task SaveLatestCandidatesAsync(CandidatesDocument candidates, CancellationToken cancellationToken) =>
        Get(AdviceContainer).UpsertItemAsync(new Envelope<CandidatesDocument>("candidates-latest", candidates), new PartitionKey("candidates-latest"), cancellationToken: cancellationToken);

    private async Task<string> SaveVersionedAsync<T>(string id, T value, string? etag, string conflict, CancellationToken cancellationToken)
    {
        var container = Get(StateContainer);
        var item = new Envelope<T>(id, value);
        try
        {
            var response = etag is null
                ? await container.CreateItemAsync(item, new PartitionKey(id), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await container.ReplaceItemAsync(item, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
            return response.ETag;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            throw new StoreConflictException(conflict);
        }
    }

    public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) =>
        CreateOnceAsync(Get(TradesContainer), new Envelope<TradeLine>(id, trade), cancellationToken);

    public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) =>
        CreateOnceAsync(Get(AdviceContainer), new Envelope<AdviceDocument>(id, advice), cancellationToken);

    public async Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) =>
        (await ReadAsync<AdviceDocument>(Get(AdviceContainer), id, cancellationToken).ConfigureAwait(false))?.Value;

    public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) =>
        Get(AdviceContainer).UpsertItemAsync(new Envelope<AdviceDocument>("advice-latest", advice), new PartitionKey("advice-latest"), cancellationToken: cancellationToken);

    private static async Task CreateOnceAsync<T>(Container container, Envelope<T> item, CancellationToken cancellationToken)
    {
        try
        {
            await container.CreateItemAsync(item, new PartitionKey(item.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            // Written by an earlier attempt of the same run: append-only records stay as they are.
        }
    }

    private static async Task<Versioned<T>?> ReadAsync<T>(Container container, string id, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<Envelope<T>>(id, new PartitionKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new Versioned<T>(response.Resource.Value, response.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
