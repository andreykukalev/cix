using System.Collections.Concurrent;
using System.Text.Json;

namespace Cix.Stocks.Host;

/// <summary>Another run changed the saved portfolio first: this run's result is dropped.</summary>
public sealed class StoreConflictException(string message) : Exception(message);

/// <summary>
/// The stocks host's own database (never the crypto engines'): cached bars, the paper portfolio, its trades and the
/// advice. Trades and dated advice are append-only: written once, never changed.
/// </summary>
public interface IStocksStore
{
    Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken);

    Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken);

    Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Saves the portfolio if it is still at <paramref name="etag"/> (null: none saved yet), and returns its new version.</summary>
    /// <exception cref="StoreConflictException">Another run saved it first.</exception>
    Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken);

    /// <summary>Adds a trade; one already there under the id (a run repeated after a failure) is left as it is.</summary>
    Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken);

    /// <summary>Adds a day's advice to the history; one already there is left as it is.</summary>
    Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken);

    /// <summary>A day's advice (<c>advice-YYYY-MM-DD</c>) or <c>advice-latest</c>; null when there is none.</summary>
    Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replaces <c>advice-latest</c>, the document the dashboard reads.</summary>
    Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken);

    /// <summary>The market-wide search's state: the last session searched and the calls it spent.</summary>
    Task<Versioned<SearchState>?> GetSearchStateAsync(CancellationToken cancellationToken);

    /// <summary>Saves the search's state if it is still at <paramref name="etag"/> (null: none saved yet).</summary>
    /// <exception cref="StoreConflictException">Another run saved it first.</exception>
    Task<string> SaveSearchStateAsync(SearchState state, string? etag, CancellationToken cancellationToken);

    /// <summary>The exchange's symbol list as last fetched; null before the first.</summary>
    Task<StoredUniverse?> GetUniverseAsync(CancellationToken cancellationToken);

    Task SaveUniverseAsync(StoredUniverse universe, CancellationToken cancellationToken);

    /// <summary>Adds a session's candidates to the history (<c>candidates-YYYY-MM-DD</c>); one already there is left as it is.</summary>
    Task AddCandidatesAsync(string id, CandidatesDocument candidates, CancellationToken cancellationToken);

    /// <summary>A session's candidates (<c>candidates-YYYY-MM-DD</c>) or <c>candidates-latest</c>; null when there is none.</summary>
    Task<CandidatesDocument?> GetCandidatesAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replaces <c>candidates-latest</c>, which the dashboard reads.</summary>
    Task SaveLatestCandidatesAsync(CandidatesDocument candidates, CancellationToken cancellationToken);

    /// <summary>The owner's real portfolio as the last run left it (ADR-0026); null before the first submission was applied.</summary>
    Task<Versioned<OwnerState>?> GetOwnerStateAsync(CancellationToken cancellationToken);

    /// <summary>Saves the owner's portfolio if it is still at <paramref name="etag"/> (null: none saved yet).</summary>
    /// <exception cref="StoreConflictException">Another run saved it first.</exception>
    Task<string> SaveOwnerStateAsync(OwnerState state, string? etag, CancellationToken cancellationToken);

    /// <summary>
    /// The owner's submission <c>portfolio-N</c> from the dashboard, in the <c>requests</c> container, which the job only
    /// reads; null when there is none under that number, a read with no submission when it can't be read as one.
    /// </summary>
    Task<SubmissionRead?> GetPortfolioSubmissionAsync(int sequence, CancellationToken cancellationToken);
}

/// <summary>A submission document that exists: its value, or null when it isn't a submission (the run refuses it).</summary>
public sealed record SubmissionRead(PortfolioSubmission? Submission)
{
    private static readonly SubmissionRead Unreadable = new((PortfolioSubmission?)null);

    /// <summary>A submission is a few kilobytes (the page sends at most 16 KB): anything far larger isn't one.</summary>
    private const int MaxBytes = 64 * 1024;

    /// <summary>
    /// A stored document <c>{ id, value }</c> read as a submission. Anything else (too large, too deeply nested, not JSON, the
    /// wrong shape) is a read with no submission, never an exception: the dashboard's writer could create any document, and
    /// one bad document must not stop the nightly run.
    /// </summary>
    public static SubmissionRead Parse(ReadOnlySpan<byte> document)
    {
        if (document.Length > MaxBytes)
            return Unreadable;
        try
        {
            using var json = JsonDocument.Parse(document.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("value", out var value))
                return Unreadable;
            return new SubmissionRead(value.Deserialize<PortfolioSubmission>(StocksJson.Options));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            return Unreadable;
        }
    }

    /// <summary>A stream's document, read up to just past the limit.</summary>
    public static async Task<SubmissionRead> ParseAsync(Stream document, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await document.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
                return Unreadable;
        }

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}

/// <summary>A store in memory, for tests and dry runs. Values go through JSON, as they would to the database.</summary>
public sealed class InMemoryStocksStore : IStocksStore
{
    private readonly ConcurrentDictionary<string, string> _docs = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private int _stateVersion;

    public IReadOnlyDictionary<string, string> Documents => _docs;

    public T? Read<T>(string key) => _docs.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, StocksJson.Options) : default;

    public Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) => Task.FromResult(Read<CachedBars>($"bars/{symbol}"));

    public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken)
    {
        _docs[$"bars/{bars.Symbol}"] = JsonSerializer.Serialize(bars, StocksJson.Options);
        return Task.CompletedTask;
    }

    public Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var state = Read<PaperState>("state/paper");
            return Task.FromResult(state is null ? null : new Versioned<PaperState>(state, _stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var exists = _docs.ContainsKey("state/paper");
            if (exists ? etag != _stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : etag is not null)
                throw new StoreConflictException("The paper portfolio changed since it was read.");
            _docs["state/paper"] = JsonSerializer.Serialize(state, StocksJson.Options);
            _stateVersion++;
            return Task.FromResult(_stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken)
    {
        _docs.TryAdd($"trades/{id}", JsonSerializer.Serialize(trade, StocksJson.Options));
        return Task.CompletedTask;
    }

    public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken)
    {
        _docs.TryAdd($"advice/{id}", JsonSerializer.Serialize(advice, StocksJson.Options));
        return Task.CompletedTask;
    }

    public Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Read<AdviceDocument>($"advice/{id}"));

    public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken)
    {
        _docs["advice/advice-latest"] = JsonSerializer.Serialize(advice, StocksJson.Options);
        return Task.CompletedTask;
    }

    private int _searchVersion;

    public Task<Versioned<SearchState>?> GetSearchStateAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var state = Read<SearchState>("state/search");
            return Task.FromResult(state is null ? null : new Versioned<SearchState>(state, _searchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    public Task<string> SaveSearchStateAsync(SearchState state, string? etag, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var exists = _docs.ContainsKey("state/search");
            if (exists ? etag != _searchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : etag is not null)
                throw new StoreConflictException("The search's state changed since it was read.");
            _docs["state/search"] = JsonSerializer.Serialize(state, StocksJson.Options);
            _searchVersion++;
            return Task.FromResult(_searchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public Task<StoredUniverse?> GetUniverseAsync(CancellationToken cancellationToken) => Task.FromResult(Read<StoredUniverse>("state/universe"));

    public Task SaveUniverseAsync(StoredUniverse universe, CancellationToken cancellationToken)
    {
        _docs["state/universe"] = JsonSerializer.Serialize(universe, StocksJson.Options);
        return Task.CompletedTask;
    }

    public Task AddCandidatesAsync(string id, CandidatesDocument candidates, CancellationToken cancellationToken)
    {
        _docs.TryAdd($"advice/{id}", JsonSerializer.Serialize(candidates, StocksJson.Options));
        return Task.CompletedTask;
    }

    public Task<CandidatesDocument?> GetCandidatesAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Read<CandidatesDocument>($"advice/{id}"));

    public Task SaveLatestCandidatesAsync(CandidatesDocument candidates, CancellationToken cancellationToken)
    {
        _docs["advice/candidates-latest"] = JsonSerializer.Serialize(candidates, StocksJson.Options);
        return Task.CompletedTask;
    }

    private int _ownerVersion;

    public Task<Versioned<OwnerState>?> GetOwnerStateAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var state = Read<OwnerState>("state/owner");
            return Task.FromResult(state is null ? null : new Versioned<OwnerState>(state, _ownerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    public Task<string> SaveOwnerStateAsync(OwnerState state, string? etag, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var exists = _docs.ContainsKey("state/owner");
            if (exists ? etag != _ownerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : etag is not null)
                throw new StoreConflictException("The owner's portfolio changed since it was read.");
            _docs["state/owner"] = JsonSerializer.Serialize(state, StocksJson.Options);
            _ownerVersion++;
            return Task.FromResult(_ownerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public Task<SubmissionRead?> GetPortfolioSubmissionAsync(int sequence, CancellationToken cancellationToken) =>
        Task.FromResult(_docs.TryGetValue(SubmissionKey(sequence), out var json) ? SubmissionRead.Parse(System.Text.Encoding.UTF8.GetBytes(json)) : null);

    /// <summary>Adds a submission as the dashboard would: <c>{ id, value }</c>, its numbers as strings.</summary>
    public void Submit(PortfolioSubmission submission) =>
        _docs[SubmissionKey(submission.Sequence)] = JsonSerializer.Serialize(
            new Envelope<PortfolioSubmission>(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"portfolio-{submission.Sequence}"), submission), StocksJson.Options);

    /// <summary>Adds a submission whose value is this raw JSON, for documents the page could send malformed.</summary>
    public void SubmitJson(int sequence, string valueJson) =>
        _docs[SubmissionKey(sequence)] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{"id":"portfolio-{{sequence}}","value":{{valueJson}}}""");

    private static string SubmissionKey(int sequence) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"requests/portfolio-{sequence}");
}
