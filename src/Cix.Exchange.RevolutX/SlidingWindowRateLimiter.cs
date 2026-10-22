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

    public TimeSpan Reserve(DateTimeOffset now)
    {
        lock (_gate)
        {
            _sent.RemoveAll(t => t <= now - Minute);

            var slot = now;
            for (var i = 0; i < 4; i++)
            {
                var moved = false;
                moved |= Push(ref slot, Second, _perSecond);
                moved |= Push(ref slot, Minute, _perMinute);
                if (!moved)
                    break;
            }

            // Keep the list sorted: a slot can be earlier than a reservation made for a later moment.
            var index = _sent.BinarySearch(slot);
            _sent.Insert(index < 0 ? ~index : index, slot);
            return slot - now;
        }
    }

    // If the window ending at `slot` is full, move `slot` to when its oldest entry expires.
    private bool Push(ref DateTimeOffset slot, TimeSpan window, int limit)
    {
        var from = slot - window;
        var inWindow = _sent.Where(t => t > from && t <= slot).ToList();
        if (inWindow.Count < limit)
            return false;

        slot = inWindow[inWindow.Count - limit] + window;
        return true;
    }
}
