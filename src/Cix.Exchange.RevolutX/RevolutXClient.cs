using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cix.Domain;
using Cix.Domain.Exchange;

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
public sealed class RevolutXClient : IExchangeClient
{
    private const int MaxPages = 50;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly RevolutXOptions _options;
    private readonly RevolutXAuth _auth;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RevolutXClient(
        HttpClient http,
        RevolutXOptions options,
        IEd25519Signer? signer = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("RevolutX:ApiKey is not configured.", nameof(options));
        if (options.MaxAttempts < 1)
            throw new ArgumentException("RevolutX:MaxAttempts must be at least 1.", nameof(options));

        _time = time ?? TimeProvider.System;
        _auth = new RevolutXAuth(options.ApiKey, signer ?? BouncyCastleEd25519Signer.FromPem(options.PrivateKeyPem), _time);
        _limiter = new SlidingWindowRateLimiter(options.RequestsPerSecond, options.RequestsPerMinute);
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    public async Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/1.0/balances", null, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseBalances);
    }

    public async Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/1.0/configuration/pairs", null, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParsePairs);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMinutes);
        var path = $"/api/1.0/candles/{Uri.EscapeDataString(RevolutXParser.NormaliseSymbol(symbol))}";
        var query = string.Create(CultureInfo.InvariantCulture, $"interval={intervalMinutes}&since={since.ToUnixTimeMilliseconds()}");

        var response = await SendAsync(HttpMethod.Get, path, query, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParseCandles);
    }

    public async Task<PlacedOrder> PlaceLimitOrderAsync(OrderIntent approvedOrder, bool postOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedOrder);
        if (approvedOrder.BaseSize <= 0 || approvedOrder.LimitPrice <= 0)
            throw new ArgumentException("Size and price must be positive.", nameof(approvedOrder));
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedOrder.ClientOrderId);

        var body = BuildLimitOrderBody(approvedOrder, postOnly);
        var response = await SendAsync(HttpMethod.Post, "/api/1.0/orders", null, body, cancellationToken, approvedOrder.ClientOrderId)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return Parse(response, RevolutXParser.ParsePlaced);
    }

    public async Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueOrderId);
        var response = await SendAsync(HttpMethod.Get, $"/api/1.0/orders/{Uri.EscapeDataString(venueOrderId)}", null, null, cancellationToken)
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
            var response = await SendAsync(HttpMethod.Get, "/api/1.0/orders/active", query, null, cancellationToken).ConfigureAwait(false);
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
        var response = await SendAsync(HttpMethod.Delete, $"/api/1.0/orders/{Uri.EscapeDataString(venueOrderId)}", null, null, cancellationToken)
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

    private sealed record RawResponse(HttpStatusCode Status, string Body);

    private async Task<RawResponse> SendAsync(
        HttpMethod method,
        string path,
        string? query,
        string? body,
        CancellationToken cancellationToken,
        string? clientOrderId = null)
    {
        var isOrderSubmission = clientOrderId is not null;

        for (var attempt = 1; ; attempt++)
        {
            var wait = _limiter.Reserve(_time.GetUtcNow());
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken).ConfigureAwait(false);

            var url = new Uri(_options.BaseAddress, query is null ? path : $"{path}?{query}");
            using var request = new HttpRequestMessage(method, url);
            foreach (var header in _auth.CreateHeaders(method.Method, path, query, body))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (isOrderSubmission)
                    throw new OrderPlacementUncertainException(clientOrderId!, ex);
                if (attempt >= _options.MaxAttempts)
                    throw new ExchangeException($"{method} {path} failed after {attempt} attempts: {ex.GetType().Name}.");

                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var status = response.StatusCode;

                if (status == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= _options.MaxAttempts)
                        throw new ExchangeException($"{method} {path} rate limited after {attempt} attempts.", 429);

                    var retryAfter = RetryAfter(response) ?? Backoff(attempt);
                    await _delay(retryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if ((int)status >= 500)
                {
                    if (isOrderSubmission)
                        throw new OrderPlacementUncertainException(clientOrderId!, new ExchangeException(Describe(method, path, status, text), (int)status));
                    if (attempt >= _options.MaxAttempts)
                        throw new ExchangeException(Describe(method, path, status, text), (int)status);

                    await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new RawResponse(status, text);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));

    /// <summary>Revolut X documents <c>Retry-After</c> in milliseconds, unlike the HTTP convention of seconds.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;

        var raw = values.FirstOrDefault();
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms < 0)
            return null;

        var delay = TimeSpan.FromMilliseconds(ms);
        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    private static void EnsureSuccess(RawResponse response)
    {
        if ((int)response.Status is >= 200 and < 300)
            return;

        var (message, errorId) = RevolutXParser.ParseError(response.Body);
        throw new ExchangeException(
            $"Revolut X returned {(int)response.Status}: {message ?? "no message"}.",
            (int)response.Status,
            errorId);
    }

    private static T Parse<T>(RawResponse response, Func<JsonElement, T> parse)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new ExchangeException($"Unexpected Revolut X response shape: {ex.Message}", (int)response.Status);
        }
    }

    private static string Describe(HttpMethod method, string path, HttpStatusCode status, string body)
    {
        var (message, _) = RevolutXParser.ParseError(body);
        return $"{method} {path} returned {(int)status}: {message ?? "no message"}.";
    }
}
