namespace Cix.Domain.Exchange;

/// <summary>
/// Marks the exchange requests made inside a scope, and in everything it awaits, as urgent: the protective exits and the
/// tickers the stops watch. The signed client keeps a share of its rate budget that only urgent requests may use, so a burst
/// of ordinary requests never holds a stop-loss sell back (pre-live assessment F-07).
/// </summary>
public static class ExchangePriority
{
    private static readonly AsyncLocal<bool> Urgent = new();

    /// <summary>Whether the request being made now is urgent.</summary>
    public static bool IsUrgent => Urgent.Value;

    /// <summary>Starts an urgent scope; disposing it restores what was before.</summary>
    public static Scope BeginUrgent()
    {
        var was = Urgent.Value;
        Urgent.Value = true;
        return new Scope(was);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly bool _was;

        internal Scope(bool was) => _was = was;

        public void Dispose() => Urgent.Value = _was;
    }
}
