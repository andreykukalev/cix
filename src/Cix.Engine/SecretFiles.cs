using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.FileProviders;

namespace Cix.Engine;

/// <summary>
/// Reads secrets from one file per setting (Docker secrets, Kubernetes or Container Apps secret volumes): a file named
/// <c>ToolServer__OperatorKey</c> sets <c>ToolServer:OperatorKey</c>. Only the names in <see cref="Allowed"/> are read, so a stray
/// file can't switch the trading mode, a risk limit or an endpoint.
/// </summary>
public static class SecretFiles
{
    public const string DefaultDirectory = "/run/secrets";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "ToolServer__OperatorKey",
        "Cosmos__AccountKey",
    };

    public static IConfigurationBuilder AddCixSecretFiles(this IConfigurationBuilder configuration, string directory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return configuration;

        return configuration.AddKeyPerFile(source =>
        {
            source.FileProvider = new PhysicalFileProvider(directory);
            source.Optional = true;
            source.IgnoreCondition = name => !Allowed.Contains(name);
        });
    }
}
