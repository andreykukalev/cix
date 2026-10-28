namespace Cix.Exchange.RevolutX;

/// <summary>
/// One rate budget for two kinds of request: urgent ones (<see cref="Cix.Domain.Exchange.ExchangePriority"/>: the protective
/// exits and the tickers the stops watch) may use all of it, the others at most <c>1 - urgentShare</c> of each window, so a
/// burst of ordinary requests always leaves room for a stop-loss sell (pre-live assessment F-07). An ordinary request waits
/// for its own share first, then for the whole budget, which counts every request. Pure given the clock, like
/// <see cref="SlidingWindowRateLimiter"/>.
/// </summary>
internal sealed class PriorityRateLimiter
{
    public const decimal MaxUrgentShare = 0.5m;

    private readonly SlidingWindowRateLimiter _all;
    private readonly SlidingWindowRateLimiter? _ordinary;

    public PriorityRateLimiter(int perSecond, int perMinute, decimal urgentShare = 0m)
    {
        if (urgentShare is < 0m or > MaxUrgentShare)
            throw new ArgumentOutOfRangeException(nameof(urgentShare), $"The urgent share must be between 0 and {MaxUrgentShare}.");
        _all = new SlidingWindowRateLimiter(perSecond, perMinute);
        var ordinaryPerSecond = perSecond - (int)Math.Ceiling(perSecond * urgentShare);
        var ordinaryPerMinute = perMinute - (int)Math.Ceiling(perMinute * urgentShare);
        if (urgentShare > 0m && ordinaryPerSecond >= 1)
            _ordinary = new SlidingWindowRateLimiter(ordinaryPerSecond, Math.Max(ordinaryPerSecond, ordinaryPerMinute));
    }

    /// <summary>Books a send slot and returns how long to wait before sending.</summary>
    public TimeSpan Reserve(DateTimeOffset now, bool urgent)
    {
        if (urgent || _ordinary is null)
            return _all.Reserve(now);
        var own = _ordinary.Reserve(now);
        return _all.Reserve(now, notBefore: now + own);
    }
}
