namespace Cix.Domain.Exchange;

/// <summary>Rounding to an exchange's size and price increments (<see cref="PairInfo.BaseStep"/>, <see cref="PairInfo.QuoteStep"/>).</summary>
public static class Steps
{
    public static decimal Floor(decimal value, decimal step) => step <= 0 ? value : Math.Floor(value / step) * step;

    public static decimal Ceiling(decimal value, decimal step) => step <= 0 ? value : Math.Ceiling(value / step) * step;

    public static bool IsMultiple(decimal value, decimal step) => step <= 0 || value % step == 0;
}
