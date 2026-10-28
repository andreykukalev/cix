namespace Cix.Engine.Strategy;

/// <summary>
/// The trading specs the owner can switch this mode to: the spec files the engine seeded (in live trading, those marked live).
/// The scanner runs each one's market filters at every scan, so a switch shows beforehand which coins enter and leave
/// trading (ADR-0021).
/// </summary>
public sealed class SwitchableSpecs
{
    private IReadOnlyList<StrategySpec> _current = [];

    public IReadOnlyList<StrategySpec> Current => Volatile.Read(ref _current);

    public void Set(IEnumerable<StrategySpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        Volatile.Write(ref _current, specs.ToList());
    }
}
