using System.Globalization;

namespace Cix.Exchange.RevolutX;

/// <summary>Signs a message with the account's Ed25519 private key. Implemented in issue #2 (NSec or BouncyCastle).</summary>
public interface IEd25519Signer
{
    /// <summary>Returns the raw 64-byte Ed25519 signature of <paramref name="message"/>.</summary>
    byte[] Sign(ReadOnlySpan<byte> message);
}

/// <summary>Builds Revolut X authentication headers.</summary>
/// <remarks>
/// Per the Revolut X REST API docs, the signed message is the concatenation, with no separators, of:
/// timestamp in milliseconds, uppercase HTTP method, path starting at <c>/api</c>,
/// query string without <c>?</c>, and the minified JSON body.
/// </remarks>
public sealed class RevolutXAuth
{
    public const string ApiKeyHeader = "X-Revx-API-Key";
    public const string TimestampHeader = "X-Revx-Timestamp";
    public const string SignatureHeader = "X-Revx-Signature";

    private readonly string _apiKey;
    private readonly IEd25519Signer _signer;
    private readonly TimeProvider _time;

    public RevolutXAuth(string apiKey, IEd25519Signer signer, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? TimeProvider.System;
    }

    public static string BuildMessage(long timestampMs, string method, string pathFromApi, string? query, string? minifiedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathFromApi);
        if (!pathFromApi.StartsWith("/api", StringComparison.Ordinal))
            throw new ArgumentException("Path must start with /api.", nameof(pathFromApi));

        return string.Concat(
            timestampMs.ToString(CultureInfo.InvariantCulture),
            method.ToUpperInvariant(),
            pathFromApi,
            query?.TrimStart('?') ?? string.Empty,
            minifiedBody ?? string.Empty);
    }

    /// <summary>Returns the three headers for one request.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> CreateHeaders(string method, string pathFromApi, string? query, string? minifiedBody)
    {
        var timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var message = BuildMessage(timestamp, method, pathFromApi, query, minifiedBody);
        var signature = Convert.ToBase64String(_signer.Sign(System.Text.Encoding.UTF8.GetBytes(message)));

        return
        [
            new(ApiKeyHeader, _apiKey),
            new(TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture)),
            new(SignatureHeader, signature),
        ];
    }
}
