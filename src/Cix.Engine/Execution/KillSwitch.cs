namespace Cix.Engine.Execution;

/// <summary>
/// Halts all new orders. Engaging persists before anything else happens; <see cref="ExecutionService.EngageKillSwitchAsync"/>
/// then cancels open orders. Only the owner releases it: no agent tool may call <see cref="Release"/>.
/// </summary>
public sealed class KillSwitch(IEngineStateStore store, TimeProvider time)
{
    /// <summary>A later cause that couldn't be saved: kept in memory until it is, so Resume still has to name it.</summary>
    private KillSwitchState? _unsavedAlso;

    public KillSwitchState State => WithUnsaved(store.Current.KillSwitch);

    private KillSwitchState WithUnsaved(KillSwitchState k) =>
        _unsavedAlso is { } also && k.Engaged ? k with { AlsoBy = also.AlsoBy, AlsoAt = also.AlsoAt, AlsoReason = also.AlsoReason } : k;

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
    /// While engaged, records a later cause beside the halt (the first reason is kept). Returns false when it isn't engaged,
    /// or the same cause with the same reason is the halt's own or the one already recorded: a finding repeated every pass
    /// stays quiet, a new one changes what the page must name.
    /// </summary>
    internal bool NoteAlso(string reason, string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var noted = false;
        KillSwitchState? next = null;
        try
        {
            store.Update(s =>
            {
                var k = WithUnsaved(s.KillSwitch);
                if (!k.Engaged || (by == k.By && reason == k.Reason) || (by == k.AlsoBy && reason == k.AlsoReason))
                    return s;
                noted = true;
                next = k with { AlsoBy = by, AlsoAt = time.GetUtcNow(), AlsoReason = reason };
                return s with { KillSwitch = next };
            });
            _unsavedAlso = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (next is not null)
                _unsavedAlso = next;
            throw;
        }

        return noted;
    }

    /// <summary>Owner only, through <see cref="ExecutionService.ReleaseKillSwitchAsync"/>. The risk gate's other limits still apply.</summary>
    internal void Release(string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        store.Update(s => s with { KillSwitch = KillSwitchState.Released });
        _unsavedAlso = null;
    }
}
