using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cix.Domain.Exchange;

namespace Cix.Exchange.RevolutX;

internal sealed record RawResponse(HttpStatusCode Status, string Body);

/// <summary>
/// HTTP layer shared by the authenticated client and the public market-data client: optional signing,
/// rate limiting and the retry policy described on <see cref="RevolutXClient"/>.
/// </summary>
internal sealed class RevolutXTransport
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly RevolutXOptions _options;
    private readonly RevolutXAuth? _auth;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="auth">Null for public endpoints: requests are sent unsigned.</param>
    public RevolutXTransport(
        HttpClient http,
        RevolutXOptions options,
        RevolutXAuth? auth,
        int requestsPerSecond,
        int requestsPerMinute,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Validate(options);
        _auth = auth;
        _time = time;
        _limiter = new SlidingWindowRateLimiter(requestsPerSecond, requestsPerMinute);
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    public static void Validate(RevolutXOptions options)
    {
        if (options.BaseAddress is not { IsAbsoluteUri: true } || options.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("RevolutX:BaseAddress must be an absolute https URL.", nameof(options));
        if (options.MaxAttempts < 1)
            throw new ArgumentException("RevolutX:MaxAttempts must be at least 1.", nameof(options));
    }


    public async Task<RawResponse> SendAsync(
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
            if (_auth is not null)
            {
                foreach (var header in _auth.CreateHeaders(method.Method, path, query, body))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            // Before dispatch nothing has been sent, so plain cancellation is accurate.
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            string text;
            try
            {
                response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                try
                {
                    text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (isOrderSubmission && ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // After dispatch, any failure (including caller cancellation) leaves the order's fate unknown.
                throw new OrderPlacementUncertainException(clientOrderId!, ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= _options.MaxAttempts)
                    throw new ExchangeException($"{method} {path} failed after {attempt} attempts: {ex.GetType().Name}.", inner: ex);

                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
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

    public static void EnsureSuccess(RawResponse response)
    {
        if ((int)response.Status is >= 200 and < 300)
            return;

        var (message, errorId) = RevolutXParser.ParseError(response.Body);
        throw new ExchangeException(
            $"Revolut X returned {(int)response.Status}: {message ?? "no message"}.",
            (int)response.Status,
            errorId);
    }

    public static T Parse<T>(RawResponse response, Func<JsonElement, T> parse)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException
                                       or OverflowException or ArgumentOutOfRangeException or KeyNotFoundException)
        {
            throw new ExchangeException(
                $"Unexpected Revolut X response shape: {RevolutXParser.Sanitize(ex.Message)}",
                (int)response.Status,
                inner: ex);
        }
    }

    private static string Describe(HttpMethod method, string path, HttpStatusCode status, string body)
    {
        var (message, _) = RevolutXParser.ParseError(body);
        return $"{method} {path} returned {(int)status}: {message ?? "no message"}.";
    }
}
