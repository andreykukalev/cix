using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.FileProviders;

namespace Cix.Engine;

/// <summary>
/// Reads secrets from one file per setting (Docker secrets, Kubernetes or Container Apps secret volumes): a file named
/// <c>ToolServer__OperatorKey</c> sets <c>ToolServer:OperatorKey</c>. Only the names in <see cref="Allowed"/> are read, so a stray
/// file can't switch the trading mode, a risk limit or an endpoint. The Revolut X keys (<see cref="LiveOnly"/>) are read in
/// Live mode only, and a Paper engine refuses to start if they are there at all, so paper keeps holding no trading
/// credentials (#68).
/// </summary>
public static class SecretFiles
{
    public const string DefaultDirectory = "/run/secrets";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "ToolServer__OperatorKey",
        "Cosmos__AccountKey",
    };

    /// <summary>The Revolut X API key and Ed25519 private key of the live sub-account.</summary>
    public static readonly IReadOnlySet<string> LiveOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "RevolutX__ApiKey",
        "RevolutX__PrivateKeyPem",
    };

    public static IConfigurationBuilder AddCixSecretFiles(this IConfigurationBuilder configuration, string directory, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return configuration;

        if (mode != TradingMode.Live)
        {
            // Names only: the message never carries a file's contents.
            var present = LiveOnly.Where(name => File.Exists(Path.Combine(directory, name))).ToList();
            if (present.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Paper mode holds no trading credentials, but {string.Join(" and ", present)} is in {directory}. Remove it; only the live engine gets the Revolut X keys.");
            }
        }

        return configuration.AddKeyPerFile(source =>
        {
            source.FileProvider = new PhysicalFileProvider(directory);
            source.Optional = true;
            source.IgnoreCondition = name => !Allowed.Contains(name) && !(mode == TradingMode.Live && LiveOnly.Contains(name));
        });
    }
}
