namespace Cix.Engine.Execution;

/// <summary>
/// Halts all new orders. Engaging persists before anything else happens; <see cref="ExecutionService.EngageKillSwitchAsync"/>
/// then cancels open orders. Only the owner releases it: no agent tool may call <see cref="Release"/>.
/// </summary>
public sealed class KillSwitch(IEngineStateStore store, TimeProvider time)
{
    public KillSwitchState State => store.Current.KillSwitch;

    public bool IsEngaged => State.Engaged;

    /// <summary>Engages and persists. Returns false if it was already engaged (the first reason is kept).</summary>
    internal bool Engage(string reason, string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var engaged = false;
        store.Update(s =>
        {
            if (s.KillSwitch.Engaged)
                return s;
            engaged = true;
            return s with { KillSwitch = new KillSwitchState(true, reason, by, time.GetUtcNow()) };
        });
        return engaged;
    }

    /// <summary>
    /// While engaged, records <paramref name="by"/> as a later cause beside the halt (the first reason is kept). Returns false
    /// when it isn't engaged, or the cause is the halt's own or the one already recorded.
    /// </summary>
    internal bool NoteAlso(string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var noted = false;
        store.Update(s =>
        {
            var k = s.KillSwitch;
            if (!k.Engaged || string.Equals(by, k.By, StringComparison.Ordinal) || string.Equals(by, k.AlsoBy, StringComparison.Ordinal))
                return s;
            noted = true;
            return s with { KillSwitch = k with { AlsoBy = by, AlsoAt = time.GetUtcNow() } };
        });
        return noted;
    }

    /// <summary>Owner only, through <see cref="ExecutionService.ReleaseKillSwitchAsync"/>. The risk gate's other limits still apply.</summary>
    internal void Release(string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        store.Update(s => s with { KillSwitch = KillSwitchState.Released });
    }
}
