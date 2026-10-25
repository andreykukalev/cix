using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cix.Engine.Persistence;

/// <summary>Bound from the "Cosmos" section. Disabled by default: the engine then keeps its earlier file and memory stores.</summary>
public sealed class CosmosOptions
{
    public const string SectionName = "Cosmos";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = "https://localhost:8081";

    /// <summary>From user secrets or Key Vault, never a file in the repo. Optional with the Linux emulator, which ignores keys.</summary>
    public string? AccountKey { get; set; }

    public string Database { get; set; } = "cix";

    /// <summary>
    /// The local emulator: gateway mode and its self-signed certificate accepted, for a loopback endpoint or the Docker Compose
    /// service <see cref="ComposeHost"/> only. Never in Live.
    /// </summary>
    public bool Emulator { get; set; }

    public void Validate()
    {
        if (!Enabled)
            return;
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Cosmos:Endpoint must be an absolute https URL.");
        if (Emulator && !IsLocalHost(uri))
            throw new InvalidOperationException(
                "Cosmos:Emulator is only allowed with a loopback endpoint or the Compose service https://cosmos:8081 (it accepts a self-signed certificate).");
        if (!Emulator && string.IsNullOrWhiteSpace(AccountKey))
            throw new InvalidOperationException("Cosmos:AccountKey is required (set it with dotnet user-secrets).");
        if (string.IsNullOrWhiteSpace(Database) || Database.Length > 64)
            throw new InvalidOperationException("Cosmos:Database must be a short name.");
    }

    /// <summary>The emulator's service name in docker-compose.yml.</summary>
    public const string ComposeHost = "cosmos";

    /// <summary>Loopback, or exactly the Compose emulator service (any other name could resolve elsewhere via DNS suffixes).</summary>
    public static bool IsLocalHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || string.Equals(uri.Host, ComposeHost, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The engine's Cosmos DB database (docs/persistence.md):
/// <list type="bullet">
/// <item><c>journal</c>: the append-only history of decisions, orders, fills and safety events, partitioned by UTC day.</item>
/// <item><c>state</c>: what is true now, one document per id: <c>engine-state-paper</c> (or <c>-live</c>), <c>paper-account</c>,
/// and the archived strategy specs <c>spec-name@vN-hash</c>. Each document says what it is in <c>type</c> and <c>about</c>.</item>
/// </list>
/// Works the same against the emulator and Azure (serverless).
/// </summary>
public sealed class CosmosDb : IDisposable
{
    public const string JournalContainer = "journal";
    public const string StateContainer = "state";

    private readonly CosmosClient _client;
    private readonly string _database;

    public CosmosDb(CosmosOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _database = options.Database;

        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "cix-engine",
            RequestTimeout = TimeSpan.FromSeconds(10), // a stuck database must not hold the engine for long
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(2),
            Serializer = new SystemTextJsonCosmosSerializer(Json),
        };
        // Gateway mode: HTTPS on port 443 only, which works from a home network to Azure as well as to the emulator (direct
        // mode would also need ports 10250-10256). It adds a few milliseconds per call, which doesn't matter at this volume.
        clientOptions.ConnectionMode = ConnectionMode.Gateway;
        if (options.Emulator)
        {
            clientOptions.LimitToEndpoint = true;
            clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                // The emulator's certificate is self-signed; Validate() allows this for loopback or the Compose emulator only.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        }

        // The Linux emulator ignores the key, but the SDK needs a well-formed one.
        var key = string.IsNullOrWhiteSpace(options.AccountKey) ? Convert.ToBase64String(new byte[64]) : options.AccountKey;
        _client = new CosmosClient(options.Endpoint, key, clientOptions);
    }

    /// <summary>
    /// Every number is written as a string and read back from one, so decimals keep their exact value: Cosmos DB would
    /// otherwise store JSON numbers as doubles (CLAUDE.md rule 7).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public Container Journal => _client.GetContainer(_database, JournalContainer);

    public Container State => _client.GetContainer(_database, StateContainer);

    /// <summary>Creates the database and containers if they don't exist yet. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Database database = await _client.CreateDatabaseIfNotExistsAsync(_database, cancellationToken: cancellationToken).ConfigureAwait(false);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(JournalContainer, "/day"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(StateContainer, "/id"), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For tests only: removes the whole database.</summary>
    internal Task DeleteDatabaseAsync(CancellationToken cancellationToken) =>
        _client.GetDatabase(_database).DeleteAsync(cancellationToken: cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// A document in the <c>state</c> container: one value per id, with <paramref name="Type"/> and <paramref name="About"/> so a
    /// person browsing the container can tell what it is, and when it last changed. Older documents may lack those three.
    /// </summary>
    internal sealed record StateDocument<T>(string Id, T Value, string? Type = null, string? About = null, DateTimeOffset? UpdatedAt = null);

    internal static async Task<T?> ReadStateAsync<T>(Container container, string id, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<StateDocument<T>>(id, new PartitionKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Resource.Value;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal static Task WriteStateAsync<T>(Container container, string id, T value, string type, string about, CancellationToken cancellationToken) =>
        container.UpsertItemAsync(new StateDocument<T>(id, value, type, about, DateTimeOffset.UtcNow), new PartitionKey(id), cancellationToken: cancellationToken);
}

/// <summary>Cosmos DB serialization through System.Text.Json with the given options.</summary>
internal sealed class SystemTextJsonCosmosSerializer(JsonSerializerOptions options) : CosmosSerializer
{
    public override T FromStream<T>(Stream stream)
    {
        if (typeof(Stream).IsAssignableFrom(typeof(T)))
            return (T)(object)stream;
        using (stream)
            return JsonSerializer.Deserialize<T>(stream, options)!;
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, options);
        stream.Position = 0;
        return stream;
    }
}
