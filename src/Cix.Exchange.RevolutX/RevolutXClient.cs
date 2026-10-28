using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;

using static Cix.Exchange.RevolutX.RevolutXTransport;

namespace Cix.Exchange.RevolutX;

/// <summary>
/// Revolut X REST client. Signs every request, keeps within the documented rate limits, and retries
/// only where a retry cannot duplicate an order.
/// </summary>
/// <remarks>
/// Retry policy:
/// <list type="bullet">
/// <item>Reads and cancels: retry on 429 (honouring <c>Retry-After</c>, documented in milliseconds), 5xx, timeouts and network errors.</item>
/// <item>Order submission: retry only on 429, which means the request was not processed. A timeout, network error or 5xx
/// throws <see cref="OrderPlacementUncertainException"/> so the caller reconciles by client order ID instead of resubmitting.</item>
/// <item>Other 4xx: never retried.</item>
/// </list>
/// </remarks>
public sealed class RevolutXClient : IExchangeClient, IQuoteSource, IBulkQuoteSource
{
    private const int MaxPages = 50;

    private readonly TimeProvider _time;

    private readonly RevolutXTransport _transport;

    public RevolutXClient(
        HttpClient http,
        RevolutXOptions options,
        IEd25519Signer? signer = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        RevolutXTransport.Validate(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("RevolutX:ApiKey is not configured.", nameof(options));

        time ??= TimeProvider.System;
        _time = time;
        var auth = new RevolutXAuth(options.ApiKey, signer ?? BouncyCastleEd25519Signer.FromPem(options.PrivateKeyPem), time);
        _transport = new RevolutXTransport(http, options, auth, options.RequestsPerSecond, options.RequestsPerMinute, time, delay);
    }

    /// <summary>
    /// Top of the order book over the signed endpoint, within the signed budget (100 a second) rather than the public one
    /// (1 a second): the live engine's quotes, and the fresh book a stop-loss sell is confirmed on.
    /// </summary>
    public async Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalised = RevolutXParser.NormaliseSymbol(symbol);
        var path = $"/api/1.0/order-book/{Uri.EscapeDataString(normalised)}";
        var response = await _transport.SendAsync(HttpMethod.Get, path, "limit=5", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var receivedAt = _time.GetUtcNow();
        return Parse(response, root => RevolutXParser.ParseOrderBookTop(root, normalised, receivedAt));
    }

    /// <summary>Every requested pair's bid and ask in one signed request (<c>/api/1.0/tickers</c>); no sizes.</summary>
    public async Task<IReadOnlyList<MarketQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var wanted = symbols.Select(RevolutXParser.NormaliseSymbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            return [];

        var query = "symbols=" + Uri.EscapeDataString(string.Join(',', wanted));
        var response = await _transport.SendAsync(HttpMethod.Get, "/api/1.0/tickers", query, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var receivedAt = _time.GetUtcNow();
        var set = wanted.ToHashSet(StringComparer.Ordinal);
        return Parse(response, root => RevolutXParser.ParseTickers(root, receivedAt)).Where(q => set.Contains(q.Symbol)).ToList();
    }

    public async Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(HttpMethod.Get, "/api/1.0/balances", null, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseBalances);
    }

    public async Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(HttpMethod.Get, "/api/1.0/configuration/pairs", null, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParsePairs);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMinutes);
        var path = $"/api/1.0/candles/{Uri.EscapeDataString(RevolutXParser.NormaliseSymbol(symbol))}";
        var query = string.Create(CultureInfo.InvariantCulture, $"interval={intervalMinutes}&since={since.ToUnixTimeMilliseconds()}");

        var response = await _transport.SendAsync(HttpMethod.Get, path, query, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseCandles);
    }

    /// <remarks>
    /// The caller must already have rounded size and price to the pair's <see cref="PairInfo.BaseStep"/> and
    /// <see cref="PairInfo.QuoteStep"/>; this client sends them unchanged.
    /// </remarks>
    public async Task<PlacedOrder> PlaceLimitOrderAsync(ApprovedOrder approvedOrder, bool postOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedOrder);
        approvedOrder.Claim();
        if (approvedOrder.BaseSize <= 0 || approvedOrder.LimitPrice <= 0)
            throw new ArgumentException("Size and price must be positive.", nameof(approvedOrder));
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedOrder.ClientOrderId);

        var body = BuildLimitOrderBody(approvedOrder.ToIntent(), postOnly);
        var response = await _transport.SendAsync(HttpMethod.Post, "/api/1.0/orders", null, body, cancellationToken, approvedOrder.ClientOrderId)
            .ConfigureAwait(false);
        EnsureSuccess(response);

        try
        {
            return Parse(response, RevolutXParser.ParsePlaced);
        }
        catch (ExchangeException ex)
        {
            // A 2xx means the exchange accepted something; if we can't read it, the order may exist.
            throw new OrderPlacementUncertainException(approvedOrder.ClientOrderId, ex);
        }
    }

    public async Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueOrderId);
        var response = await _transport.SendAsync(HttpMethod.Get, $"/api/1.0/orders/{Uri.EscapeDataString(venueOrderId)}", null, null, cancellationToken)
            .ConfigureAwait(false);

        // Seen in the spike: a just-placed order can briefly return 404. Callers treat null as "not visible yet".
        if (response.Status == HttpStatusCode.NotFound)
            return null;

        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseOrder);
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken)
    {
        var wanted = symbol is null ? null : RevolutXParser.NormaliseSymbol(symbol);
        var all = new List<ExchangeOrder>();
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var query = cursor is null ? null : "cursor=" + Uri.EscapeDataString(cursor);
            var response = await _transport.SendAsync(HttpMethod.Get, "/api/1.0/orders/active", query, null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);

            var (orders, next) = Parse(response, RevolutXParser.ParseOrderPage);
            all.AddRange(wanted is null ? orders : orders.Where(o => o.Symbol == wanted));
            if (next is null || next == cursor)
                return all;
            cursor = next;
        }

        throw new ExchangeException($"Active orders exceeded {MaxPages} pages; refusing to continue.");
    }

    public async Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueOrderId);
        var response = await _transport.SendAsync(HttpMethod.Delete, $"/api/1.0/orders/{Uri.EscapeDataString(venueOrderId)}", null, null, cancellationToken)
            .ConfigureAwait(false);

        if (response.Status == HttpStatusCode.NotFound)
            return false;

        EnsureSuccess(response);
        return true;
    }

    internal static string BuildLimitOrderBody(OrderIntent order, bool postOnly)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("client_order_id", order.ClientOrderId);
            w.WriteString("symbol", RevolutXParser.NormaliseSymbol(order.Symbol));
            w.WriteString("side", order.Side == OrderSide.Buy ? "BUY" : "SELL");
            w.WriteStartObject("order_configuration");
            w.WriteStartObject("limit");
            w.WriteString("base_size", order.BaseSize.ToString(CultureInfo.InvariantCulture));
            w.WriteString("price", order.LimitPrice.ToString(CultureInfo.InvariantCulture));
            if (order.TimeInForce == TimeInForce.ImmediateOrCancel)
                w.WriteString("time_in_force", "ioc"); // the exchange cancels whatever doesn't fill at once
            if (postOnly)
            {
                w.WriteStartArray("execution_instructions");
                w.WriteStringValue("post_only");
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
