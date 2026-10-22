using System.Globalization;
using Cix.Domain.Exchange;
using static Cix.Exchange.RevolutX.RevolutXTransport;

namespace Cix.Exchange.RevolutX;

/// <summary>
/// Revolut X public (unauthenticated) market data: pairs and candles. Needs no API key, holds no signer and has
/// no order methods, so paper trading runs without any trading credentials on the machine.
/// </summary>
/// <remarks>Public endpoints allow 1 request per second, so callers should cache pairs and poll candles sparingly.</remarks>
public sealed class RevolutXPublicMarketData : IMarketDataSource
{
    private readonly RevolutXTransport _transport;
    private readonly string? _region;

    public RevolutXPublicMarketData(
        HttpClient http,
        RevolutXOptions options,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _region = string.IsNullOrWhiteSpace(options.PublicRegion) ? null : options.PublicRegion.Trim().ToUpperInvariant();
        _transport = new RevolutXTransport(http, options, auth: null, requestsPerSecond: 1, requestsPerMinute: 60, time ?? TimeProvider.System, delay);
    }

    public async Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(HttpMethod.Get, "/api/1.0/public/configuration/pairs", RegionQuery(null), null, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParsePairs);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMinutes);
        var path = $"/api/1.0/public/candles/{Uri.EscapeDataString(RevolutXParser.NormaliseSymbol(symbol))}";
        var query = RegionQuery(string.Create(CultureInfo.InvariantCulture, $"interval={intervalMinutes}&since={since.ToUnixTimeMilliseconds()}"));

        var response = await _transport.SendAsync(HttpMethod.Get, path, query, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseCandles);
    }

    private string? RegionQuery(string? query) =>
        _region is null ? query : string.IsNullOrEmpty(query) ? $"region={_region}" : $"{query}&region={_region}";
}
