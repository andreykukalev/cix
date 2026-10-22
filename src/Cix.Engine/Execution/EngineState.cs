using System.Text.Json;

namespace Cix.Engine.Execution;

/// <summary>Whether trading is halted, why, by whom and since when.</summary>
public sealed record KillSwitchState(bool Engaged, string? Reason, string? By, DateTimeOffset? Since)
{
    public static readonly KillSwitchState Released = new(false, null, null, null);
}

/// <summary>
/// State that must survive a restart until the journal lands (#6): the kill switch and the baselines for drawdown and
/// daily loss. Losing them would silently re-enable trading or reset the loss limits.
/// </summary>
public sealed record EngineState(KillSwitchState KillSwitch, decimal PeakEquity, DateOnly? Day, decimal DayStartEquity)
{
    public static readonly EngineState Initial = new(KillSwitchState.Released, 0m, null, 0m);
}

public interface IEngineStateStore
{
    EngineState Current { get; }

    /// <summary>Applies <paramref name="change"/> atomically and persists the result before returning it.</summary>
    EngineState Update(Func<EngineState, EngineState> change);
}

/// <summary>For paper trading, whose simulated balances are in memory too: both reset together on restart.</summary>
public sealed class InMemoryEngineStateStore : IEngineStateStore
{
    private readonly Lock _gate = new();

    public EngineState Current { get; private set; } = EngineState.Initial;

    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Current = change(Current);
            return Current;
        }
    }
}

/// <summary>
/// JSON file for live trading. Written to a temporary file and moved into place, so a crash never leaves half a file.
/// An unreadable file stops startup rather than starting with the kill switch released.
/// </summary>
public sealed class FileEngineStateStore : IEngineStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly string _path;

    public FileEngineStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Current = Load(_path);
    }

    public EngineState Current { get; private set; }

    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Current);
            if (next == Current)
                return Current;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
            Current = next;
            return Current;
        }
    }

    private static EngineState Load(string path)
    {
        if (!File.Exists(path))
            return EngineState.Initial;

        try
        {
            return JsonSerializer.Deserialize<EngineState>(File.ReadAllText(path), Json)
                ?? throw new InvalidOperationException("The engine state file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The engine state file '{path}' is unreadable; fix or remove it deliberately.", ex);
        }
    }
}
