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
    /// <summary>What the strategy is for, in a sentence, for the owner's dashboard. Agents don't need it.</summary>
    public string? Purpose { get; init; }

    /// <summary>Its risk profile in plain words, for the dashboard: what it risks, never what it may earn.</summary>
    public string? RiskProfile { get; init; }

    /// <summary>
    /// First 12 hex characters of a SHA-256: of the file content for a spec loaded from a file, or of
    /// <see cref="StrategyHash.Canonical"/> for one read from the database.
    /// </summary>
    [JsonIgnore]
    public string Hash { get; init; } = string.Empty;

    /// <summary><c>name@vN#hash</c>, as written to the journal.</summary>
    [JsonIgnore]
    public string Label => $"{Name}@v{Version}#{Hash}";

    public bool Allows(string tool) => AllowedTools.Contains(tool, StringComparer.Ordinal);
}

/// <summary>Content hashes for specs that don't come from a file: the same content always gives the same hash.</summary>
public static class StrategyHash
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The spec as compact JSON (camelCase, parameters as written), without its hash.</summary>
    public static byte[] CanonicalBytes(StrategySpec spec) => JsonSerializer.SerializeToUtf8Bytes(spec, Canonical);

    public static string Of(StrategySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(spec)))[..12];
    }

    /// <summary>The spec with <see cref="StrategySpec.Hash"/> set from its content.</summary>
    public static StrategySpec WithHash(StrategySpec spec) => spec with { Hash = Of(spec) };
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
/// The active spec, which can change while the engine runs: it starts with the configured file (or nothing) and the
/// <see cref="StrategySelectionService"/> replaces it with a validated version from the database. Readers see one spec
/// or the other, never half of each; each scan and each tool call reads it once.
/// </summary>
public sealed class RuntimeStrategySpecs(StrategySpec? initial) : IStrategySpecStore
{
    private StrategySpec? _active = initial;

    public StrategySpec? Active => Volatile.Read(ref _active);

    /// <summary>When the active spec last changed at run time, or null if it is still the one the engine started with.</summary>
    public DateTimeOffset? ActivatedAt { get; private set; }

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    /// <summary>How many times the spec has changed at run time.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Activate(StrategySpec spec, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Volatile.Write(ref _active, spec);
        ActivatedAt = at;
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Completes once the spec has changed since <paramref name="seen"/> (a <see cref="Version"/>): at once if it already has,
    /// so a switch made while the caller was busy isn't missed. The scanner rescans on it.
    /// </summary>
    public Task WaitForChangeAsync(long seen, CancellationToken cancellationToken)
    {
        var changed = Volatile.Read(ref _changed); // before the version: an Activate in between shows in the version
        return Version != seen ? Task.CompletedTask : changed.Task.WaitAsync(cancellationToken);
    }
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
            return; // no strategy configured: agent tools refuse
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Name, "^[a-z0-9-]{1,40}$") || options.Version is < 1)
            throw new InvalidOperationException("Strategy:Name must be lowercase letters, digits and dashes, and Strategy:Version at least 1.");

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
