namespace Cix.Engine.Execution;

/// <summary>
/// A lock with one holder at a time that lets urgent waiters (the protective exits) in before the others, so a stop-loss sell
/// never queues behind agent orders, a reconciliation or the kill switch's cancels (pre-live assessment F-06). Waiters of the
/// same kind go in the order they came. Not reentrant.
/// </summary>
internal sealed class PriorityGate
{
    private readonly Lock _sync = new();
    private readonly LinkedList<TaskCompletionSource> _urgent = new();
    private readonly LinkedList<TaskCompletionSource> _normal = new();
    private bool _held;

    /// <summary>Whether an urgent waiter is queued: the holder should then finish quickly, its requests urgent too.</summary>
    public bool UrgentWaiting
    {
        get
        {
            lock (_sync)
                return _urgent.Count > 0;
        }
    }

    /// <summary>Waits for the gate. A cancelled wait never takes it; a wait already let in holds it, to be released.</summary>
    public Task WaitAsync(CancellationToken cancellationToken, bool urgent = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource> node;
        lock (_sync)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            node = (urgent ? _urgent : _normal).AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return cancellationToken.CanBeCanceled ? WaitCancellableAsync(node, cancellationToken) : node.Value.Task;
    }

    private async Task WaitCancellableAsync(LinkedListNode<TaskCompletionSource> node, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                if (node.List is null)
                    return; // let in already: the caller holds the gate
                node.List.Remove(node);
            }

            node.Value.TrySetCanceled(cancellationToken);
        }))
        {
            await node.Value.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Lets the next waiter in: the first urgent one, else the first of the others.</summary>
    public void Release()
    {
        TaskCompletionSource next;
        lock (_sync)
        {
            if (!_held)
                throw new SemaphoreFullException("The gate is not held.");
            var waiters = _urgent.Count > 0 ? _urgent : _normal;
            if (waiters.First is not { } first)
            {
                _held = false;
                return;
            }

            waiters.RemoveFirst();
            next = first.Value;
        }

        next.TrySetResult();
    }
}
