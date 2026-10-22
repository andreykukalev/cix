using System.Text.Json;

namespace Cix.Engine.Execution;

/// <summary>Whether trading is halted, why, by whom and since when.</summary>
public sealed record KillSwitchState(bool Engaged, string? Reason, string? By, DateTimeOffset? Since)
{
    public static readonly KillSwitchState Released = new(false, null, null, null);
}

/// <summary>
/// State that must survive a restart until the journal lands (#6): the kill switch, the baselines for drawdown and
/// daily loss, and the client order IDs of orders the engine sent that may still be open. Losing them would silently
/// re-enable trading, reset the loss limits, or make the engine's own orders look like someone else's after a restart.
/// </summary>
public sealed record EngineState(
    KillSwitchState KillSwitch,
    decimal PeakEquity,
    DateOnly? Day,
    decimal DayStartEquity,
    IReadOnlyList<string>? KnownClientOrderIds = null)
{
    /// <summary>Client order IDs the engine sent and hasn't yet seen finish; recorded before each send.</summary>
    public IReadOnlyList<string> Known => KnownClientOrderIds ?? [];

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
/// JSON file for live trading. Written to a temporary file, flushed to disk and moved into place, so a crash never
/// leaves half a file. Fails closed: an unreadable file stops startup, a missing file starts with the kill switch
/// engaged (a lost or moved file must not silently resume trading), and a failed write never keeps the switch released.
/// </summary>
public sealed class FileEngineStateStore : IEngineStateStore
{
    public const string NewFileReason = "No engine state file was found; the owner must confirm before trading starts.";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly string _path;

    public FileEngineStateStore(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The engine state file needs an absolute path.", nameof(path));

        _path = path;
        if (File.Exists(_path))
        {
            Current = Load(_path);
        }
        else
        {
            Current = EngineState.Initial with { KillSwitch = new KillSwitchState(true, NewFileReason, "engine", time.GetUtcNow()) };
            Write(Current);
        }
    }

    public EngineState Current { get; private set; }

    /// <summary>
    /// Persists, then publishes. Engaging the kill switch is the exception: it takes effect in memory first, so a disk
    /// that can't be written never leaves trading running (the write error still surfaces to the caller).
    /// </summary>
    public EngineState Update(Func<EngineState, EngineState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Current);
            if (next == Current)
                return Current;

            if (next.KillSwitch.Engaged && !Current.KillSwitch.Engaged)
                Current = next;

            Write(next);
            Current = next;
            return Current;
        }
    }

    private void Write(EngineState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static EngineState Load(string path)
    {

        try
        {
            var state = JsonSerializer.Deserialize<EngineState>(File.ReadAllText(path), Json);
            if (state?.KillSwitch is null)
                throw new InvalidOperationException($"The engine state file '{path}' has no kill switch state; fix or remove it deliberately.");
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The engine state file '{path}' is unreadable; fix or remove it deliberately.", ex);
        }
    }
}
