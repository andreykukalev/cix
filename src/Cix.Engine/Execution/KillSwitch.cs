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

    /// <summary>Owner only. Resumes trading; the risk gate's other limits still apply.</summary>
    public void Release(string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        store.Update(s => s with { KillSwitch = KillSwitchState.Released });
    }
}
