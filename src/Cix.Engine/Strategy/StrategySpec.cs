using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cix.Engine.Strategy;

public enum SpecStatus
{
    Draft,
    Paper,
    Live,
}

/// <summary>
/// A versioned strategy: the instructions agents follow, their parameters and which tools they may call. Versions are
/// immutable by convention: a change is a new file with a higher version. Every journal entry records the version and a
/// hash of its content, so an edited file is visible in the record.
/// </summary>
public sealed record StrategySpec(
    string Name,
    int Version,
    SpecStatus Status,
    string Instructions,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlyList<string> AllowedTools,
    string CreatedBy)
{
    /// <summary>First 12 hex characters of the SHA-256 of the file content.</summary>
    [JsonIgnore]
    public string Hash { get; init; } = string.Empty;

    /// <summary><c>name@vN#hash</c>, as written to the journal.</summary>
    [JsonIgnore]
    public string Label => $"{Name}@v{Version}#{Hash}";

    public bool Allows(string tool) => AllowedTools.Contains(tool, StringComparer.Ordinal);
}

/// <summary>Bound from the "Strategy" section: which spec file is active.</summary>
public sealed class StrategyOptions
{
    public const string SectionName = "Strategy";

    /// <summary>Folder of spec files named <c>name.vN.json</c>. Relative paths resolve against the app's base directory.</summary>
    public string Directory { get; set; } = "strategies";

    public string? Name { get; set; }

    public int? Version { get; set; }
}

public interface IStrategySpecStore
{
    /// <summary>The spec the engine runs, or null if none is configured.</summary>
    StrategySpec? Active { get; }
}

/// <summary>
/// Loads the configured spec file once at startup. It must exist, parse, match its file name, and suit the trading mode:
/// Live runs only a <see cref="SpecStatus.Live"/> spec; Paper runs a paper or live one; drafts never run.
/// </summary>
public sealed class FileStrategySpecStore : IStrategySpecStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public FileStrategySpecStore(StrategyOptions options, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Name) || options.Version is null)
            return; // no strategy configured: read-only tools still work, agent tools refuse

        var directory = Path.IsPathFullyQualified(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);
        Active = Load(Path.Combine(directory, $"{options.Name}.v{options.Version}.json"), options.Name, options.Version.Value, mode);
    }

    public StrategySpec? Active { get; }

    public static StrategySpec Load(string path, string name, int version, TradingMode mode)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Strategy spec file '{path}' was not found.");

        var bytes = File.ReadAllBytes(path);
        StrategySpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<StrategySpec>(bytes, Json)
                ?? throw new InvalidOperationException($"Strategy spec file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Strategy spec file '{path}' is not valid JSON.", ex);
        }

        if (!string.Equals(spec.Name, name, StringComparison.Ordinal) || spec.Version != version)
            throw new InvalidOperationException($"'{path}' declares {spec.Name} v{spec.Version}, not {name} v{version}.");
        if (string.IsNullOrWhiteSpace(spec.Instructions) || spec.AllowedTools is null || spec.Parameters is null)
            throw new InvalidOperationException($"Strategy spec {name} v{version} needs instructions, parameters and allowedTools.");
        var runnable = mode == TradingMode.Live ? spec.Status == SpecStatus.Live : spec.Status is SpecStatus.Paper or SpecStatus.Live;
        if (!runnable)
            throw new InvalidOperationException($"Strategy spec {name} v{version} is {spec.Status}; {mode} trading can't run it.");

        return spec with { Hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12] };
    }
}
