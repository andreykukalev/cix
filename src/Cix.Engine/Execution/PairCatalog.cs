using Cix.Domain.Exchange;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>Exchange trading rules per symbol, cached for an hour (they rarely change; pairs are a public, rate-limited call).</summary>
public sealed class PairCatalog(IMarketDataSource source, TimeProvider time)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyDictionary<string, PairInfo>? _pairs;
    private DateTimeOffset _loadedAt;

    /// <summary>The pair's rules, or null if the exchange doesn't list the symbol.</summary>
    public async Task<PairInfo?> GetAsync(string symbol, CancellationToken cancellationToken)
    {
        var pairs = _pairs;
        if (pairs is null || time.GetUtcNow() - _loadedAt >= RefreshInterval)
            pairs = await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return pairs.GetValueOrDefault(MarketDataPoller.NormaliseSymbol(symbol));
    }

    private async Task<IReadOnlyDictionary<string, PairInfo>> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pairs is not null && time.GetUtcNow() - _loadedAt < RefreshInterval)
                return _pairs;

            var list = await source.GetPairsAsync(cancellationToken).ConfigureAwait(false);
            _pairs = list.ToDictionary(p => MarketDataPoller.NormaliseSymbol(p.Symbol), StringComparer.Ordinal);
            _loadedAt = time.GetUtcNow();
            return _pairs;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
