namespace Cix.Domain.Exchange;

/// <summary>
/// Marks the exchange requests made inside a scope, and in everything it awaits, as urgent: the protective exits and the
/// tickers the stops watch. The signed client keeps a share of its rate budget that only urgent requests may use, so a burst
/// of ordinary requests never holds a stop-loss sell back (pre-live assessment F-07).
/// </summary>
public static class ExchangePriority
{
    private static readonly AsyncLocal<bool> Urgent = new();
    private static readonly AsyncLocal<Func<bool>?> UrgentWhen = new();

    /// <summary>Whether the request being made now is urgent.</summary>
    public static bool IsUrgent => Urgent.Value || UrgentWhen.Value?.Invoke() == true;

    /// <summary>Starts an urgent scope; disposing it restores what was before.</summary>
    public static Scope BeginUrgent()
    {
        var scope = new Scope(Urgent.Value, UrgentWhen.Value);
        Urgent.Value = true;
        return scope;
    }

    /// <summary>
    /// Starts a scope whose requests are urgent while <paramref name="condition"/> holds, checked as each request is made:
    /// for work that holds what a stop-loss sell waits for, so it isn't held back by the ordinary share meanwhile.
    /// Disposing it restores what was before.
    /// </summary>
    public static Scope BeginUrgentWhen(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var scope = new Scope(Urgent.Value, UrgentWhen.Value);
        UrgentWhen.Value = condition;
        return scope;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly bool _was;
        private readonly Func<bool>? _whenWas;

        internal Scope(bool was, Func<bool>? whenWas)
        {
            _was = was;
            _whenWas = whenWas;
        }

        public void Dispose()
        {
            Urgent.Value = _was;
            UrgentWhen.Value = _whenWas;
        }
    }
}
