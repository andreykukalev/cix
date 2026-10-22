namespace Cix.Exchange.RevolutX;

/// <summary>
/// Connection settings. Load <see cref="ApiKey"/> and <see cref="PrivateKeyPem"/> from user secrets
/// (local) or Key Vault (Azure) only, never from files in the repo.
/// </summary>
public sealed class RevolutXOptions
{
    public const string SectionName = "RevolutX";

    public Uri BaseAddress { get; set; } = new("https://revx.revolut.com");

    /// <summary>The 64-character API key of the bot's sub-account.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>PKCS#8 PEM of the Ed25519 private key.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    public int RequestsPerSecond { get; set; } = 100;

    public int RequestsPerMinute { get; set; } = 1_000;

    /// <summary>Attempts per request, including the first, for retryable failures.</summary>
    public int MaxAttempts { get; set; } = 4;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Never prints credentials.</summary>
    public override string ToString() =>
        $"RevolutXOptions {{ BaseAddress = {BaseAddress}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "<missing>" : "<set>")}, PrivateKeyPem = {(string.IsNullOrEmpty(PrivateKeyPem) ? "<missing>" : "<set>")} }}";
}
