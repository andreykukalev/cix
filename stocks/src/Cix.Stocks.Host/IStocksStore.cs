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

    /// <summary>Saves the portfolio if it is still at <paramref name="etag"/> (null: none saved yet).</summary>
    /// <exception cref="StoreConflictException">Another run saved it first.</exception>
    Task SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken);

    /// <summary>Adds a trade; one already there under the id (a run repeated after a failure) is left as it is.</summary>
    Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken);

    /// <summary>Adds a day's advice to the history; one already there is left as it is.</summary>
    Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken);

    /// <summary>Replaces <c>advice-latest</c>, the document the dashboard reads.</summary>
    Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken);
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

    public Task SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var exists = _docs.ContainsKey("state/paper");
            if (exists ? etag != _stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : etag is not null)
                throw new StoreConflictException("The paper portfolio changed since it was read.");
            _docs["state/paper"] = JsonSerializer.Serialize(state, StocksJson.Options);
            _stateVersion++;
        }

        return Task.CompletedTask;
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

    public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken)
    {
        _docs["advice/advice-latest"] = JsonSerializer.Serialize(advice, StocksJson.Options);
        return Task.CompletedTask;
    }
}
