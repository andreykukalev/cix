using Cix.Domain;
using Cix.Domain.Risk;
using System.Net;
using System.Text;

namespace Cix.Exchange.RevolutX.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);

/// <summary>Returns queued responses in order and records every request. Never touches the network.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string? body = null, string? retryAfter = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });
        return this;
    }

    public FakeHandler RespondFixture(string name, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(status, Fixture.Read(name));

    public FakeHandler Throw(Exception ex)
    {
        _responses.Enqueue(() => throw ex);
        return this;
    }

    /// <summary>Runs arbitrary behaviour when the request arrives (for example, cancelling mid-flight).</summary>
    public FakeHandler OnSend(Func<HttpResponseMessage> behaviour)
    {
        _responses.Enqueue(behaviour);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Content?.Headers.ContentType is { } contentType)
            headers["Content-Type"] = contentType.ToString();
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        if (_responses.Count == 0)
            throw new InvalidOperationException("No queued response for " + request.RequestUri);
        return _responses.Dequeue()();
    }
}

internal sealed class RecordingSigner : IEd25519Signer
{
    public List<string> Messages { get; } = [];

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        Messages.Add(Encoding.UTF8.GetString(message));
        return [1, 2, 3];
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class Fixture
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Builds a client wired to fakes; collects requested delays instead of waiting.</summary>
internal sealed class ClientHarness
{
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1792677504427);

    public FakeHandler Handler { get; } = new();
    public RecordingSigner Signer { get; } = new();
    public List<TimeSpan> Delays { get; } = [];
    public RevolutXOptions Options { get; } = new() { ApiKey = "test-api-key-not-real", MaxAttempts = 3 };

    public RevolutXClient Create() => new(
        new HttpClient(Handler),
        Options,
        Signer,
        new FixedTime(Now),
        (delay, _) =>
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        });
}

/// <summary>Builds an <see cref="ApprovedOrder"/> without the gate, to test how clients handle orders the gate would refuse.</summary>
internal static class TestApproval
{
    public static ApprovedOrder Approved(this OrderIntent order) => new(order, DateTimeOffset.UnixEpoch);
}
