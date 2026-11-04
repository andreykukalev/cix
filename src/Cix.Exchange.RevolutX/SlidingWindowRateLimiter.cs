namespace Cix.Exchange.RevolutX;

/// <summary>
/// Client-side limiter for two sliding windows (per second and per minute).
/// <see cref="Reserve"/> books a send slot and returns how long to wait before sending.
/// Pure and deterministic given the clock value, so it is easy to test.
/// </summary>
internal sealed class SlidingWindowRateLimiter
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly int _perSecond;
    private readonly int _perMinute;
    private readonly List<DateTimeOffset> _sent = [];
    private readonly Lock _gate = new();

    public SlidingWindowRateLimiter(int perSecond, int perMinute)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, perSecond);
        _perSecond = perSecond;
        _perMinute = perMinute;
    }

    public TimeSpan Reserve(DateTimeOffset now) => Reserve(now, now);

    /// <summary>
    /// Books a send slot no earlier than <paramref name="notBefore"/> and returns how long from <paramref name="now"/> to wait.
    /// Only sends older than a minute before <paramref name="now"/> are forgotten, so booking ahead never drops sends still in
    /// the current window.
    /// </summary>
    public TimeSpan Reserve(DateTimeOffset now, DateTimeOffset notBefore)
    {
        lock (_gate)
        {
            _sent.RemoveAll(t => t <= now - Minute);

            // Each push moves the slot strictly later, past one booked entry, so this terminates.
            var slot = notBefore > now ? notBefore : now;
            for (var i = 0; i <= _sent.Count + 1; i++)
            {
                var next = Push(Push(slot, Second, _perSecond), Minute, _perMinute);
                if (next == slot)
                    break;
                slot = next;
            }

            // Keep the list sorted: a slot can be earlier than a reservation made for a later moment.
            var index = _sent.BinarySearch(slot);
            _sent.Insert(index < 0 ? ~index : index, slot);
            return slot - now;
        }
    }

    // If the window ending at `slot` is full, returns when its oldest blocking entry expires; otherwise `slot`.
    private DateTimeOffset Push(DateTimeOffset slot, TimeSpan window, int limit)
    {
        var from = slot - window;
        var inWindow = new List<DateTimeOffset>();
        foreach (var t in _sent)
        {
            if (t > from && t <= slot)
                inWindow.Add(t);
        }

        return inWindow.Count < limit ? slot : inWindow[inWindow.Count - limit] + window;
    }
}
