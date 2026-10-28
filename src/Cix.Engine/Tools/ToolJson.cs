using System.Text.Json;
using System.Text.Json.Serialization;
using Cix.Engine.MarketData;
using ModelContextProtocol;

namespace Cix.Engine.Tools;

/// <summary>Tool results are JSON text: camelCase, enums as strings, nulls omitted.</summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static string Of<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Normalises and checks a symbol argument against the watched pairs the risk gate reads.</summary>
    public static string Symbol(string? symbol, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 20)
            throw new McpException("symbol is required, for example BTC-EUR.");
        var normalised = MarketDataPoller.NormaliseSymbol(symbol);
        if (!allowed.Contains(normalised))
            throw new McpException($"{normalised} is not tradable here; allowed: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        return normalised;
    }

    public static DateOnly? Date(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", out var day)
            ? day
            : throw new McpException($"{name} must be a date like 2026-10-22.");
    }
}
