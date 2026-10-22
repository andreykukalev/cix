using System.Globalization;
using System.Text.Json;
using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Exchange.RevolutX;

/// <summary>
/// Parses Revolut X JSON into domain types and smooths over its inconsistencies
/// (found in the spike, issue #1): <c>BTC/EUR</c> vs <c>BTC-EUR</c>, <c>buy</c> vs <c>BUY</c>,
/// <c>state</c> vs <c>status</c>, <c>id</c> vs <c>venue_order_id</c>. All numbers arrive as strings.
/// </summary>
internal static class RevolutXParser
{
    public static string NormaliseSymbol(string symbol) => symbol.Trim().Replace('/', '-').ToUpperInvariant();

    public static IReadOnlyList<Balance> ParseBalances(JsonElement root) =>
        Rows(root).Select(b => new Balance(
            Str(b, "currency"),
            Dec(b, "available"),
            Dec(b, "reserved"),
            Dec(b, "total"))).ToList();

    /// <summary>The pairs endpoint returns an object keyed by <c>BASE/QUOTE</c>; an array is accepted too.</summary>
    public static IReadOnlyList<PairInfo> ParsePairs(JsonElement root)
    {
        var container = Data(root);
        IEnumerable<JsonElement> items = container.ValueKind == JsonValueKind.Object
            ? container.EnumerateObject().Select(p => p.Value)
            : Rows(root);

        return items
            .Where(p => p.ValueKind == JsonValueKind.Object)
            .Select(p =>
            {
                var baseCcy = Str(p, "base");
                var quoteCcy = Str(p, "quote");
                return new PairInfo(
                    $"{baseCcy}-{quoteCcy}".ToUpperInvariant(),
                    baseCcy,
                    quoteCcy,
                    Dec(p, "base_step"),
                    Dec(p, "quote_step"),
                    Dec(p, "min_order_size"),
                    Dec(p, "max_order_size"),
                    DecOrNull(p, "min_order_size_quote") ?? 0m,
                    string.Equals(OptStr(p, "status"), "active", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    public static IReadOnlyList<Candle> ParseCandles(JsonElement root) =>
        Rows(root).Select(c => new Candle(
            Time(c, "start"),
            Dec(c, "open"),
            Dec(c, "high"),
            Dec(c, "low"),
            Dec(c, "close"),
            Dec(c, "volume"))).ToList();

    public static PlacedOrder ParsePlaced(JsonElement root)
    {
        var data = Data(root);
        return new PlacedOrder(
            OptStr(data, "venue_order_id") ?? Str(data, "id"),
            Str(data, "client_order_id"));
    }

    public static ExchangeOrder ParseOrder(JsonElement element)
    {
        var o = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : element;

        var rawStatus = OptStr(o, "status") ?? OptStr(o, "state") ?? "unknown";
        return new ExchangeOrder(
            OptStr(o, "id") ?? Str(o, "venue_order_id"),
            OptStr(o, "client_order_id") ?? string.Empty,
            NormaliseSymbol(Str(o, "symbol")),
            ParseSide(Str(o, "side")),
            Dec(o, "quantity"),
            DecOrNull(o, "filled_quantity") ?? 0m,
            DecOrNull(o, "filled_amount") ?? 0m,
            DecOrNull(o, "price"),
            DecOrNull(o, "total_fee") ?? 0m,
            OptStr(o, "fee_currency"),
            ParseStatus(rawStatus),
            rawStatus,
            Time(o, "created_date"),
            o.TryGetProperty("updated_date", out _) ? Time(o, "updated_date") : Time(o, "created_date"));
    }

    public static (IReadOnlyList<ExchangeOrder> Orders, string? NextCursor) ParseOrderPage(JsonElement root)
    {
        var orders = Rows(root).Select(ParseOrder).ToList();
        string? cursor = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("next_cursor", out var next)
            && next.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(next.GetString()))
        {
            cursor = next.GetString();
        }

        return (orders, cursor);
    }

    public static OrderSide ParseSide(string side) => side.Trim().ToUpperInvariant() switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => throw new FormatException($"Unknown order side '{Sanitize(side, 20)}'."),
    };

    public static OrderStatus ParseStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "new" or "pending_new" or "open" or "accepted" => OrderStatus.New,
        "partially_filled" or "partially-filled" => OrderStatus.PartiallyFilled,
        "filled" => OrderStatus.Filled,
        "cancelled" or "canceled" => OrderStatus.Cancelled,
        "rejected" => OrderStatus.Rejected,
        _ => OrderStatus.Unknown,
    };

    /// <summary>Error text and ID from an error body, both sanitised: exchange text is untrusted input.</summary>
    public static (string? Message, string? ErrorId) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = OptStr(doc.RootElement, "message");
            var errorId = OptStr(doc.RootElement, "error_id");
            return (message is null ? null : Sanitize(message), errorId is null ? null : Sanitize(errorId, 64));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Makes exchange-supplied text safe to put in exception messages and logs: keeps letters, digits,
    /// spaces and basic punctuation, drops everything else (control characters, markup, braces), and caps the length.
    /// </summary>
    public static string Sanitize(string text, int maxLength = 200)
    {
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (builder.Length >= maxLength)
                break;
            if (char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or ':' or ';' or '-' or '_' or '\'' or '(' or ')' or '/' or '%')
                builder.Append(c);
        }

        return builder.Length < text.Length && text.Length > maxLength ? builder.Append('…').ToString() : builder.ToString();
    }

    private static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        var data = Data(root);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];
    }

    private static string Str(JsonElement e, string name) =>
        OptStr(e, name) ?? throw new FormatException($"Missing field '{name}'.");

    private static string? OptStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static decimal Dec(JsonElement e, string name) =>
        DecOrNull(e, name) ?? throw new FormatException($"Missing number '{name}'.");

    private static decimal? DecOrNull(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(v.GetString()) =>
                decimal.Parse(v.GetString()!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
            JsonValueKind.Number => v.GetDecimal(),
            _ => null,
        };
    }

    private static DateTimeOffset Time(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            throw new FormatException($"Missing timestamp '{name}'.");

        var ms = v.ValueKind == JsonValueKind.String
            ? long.Parse(v.GetString()!, CultureInfo.InvariantCulture)
            : v.GetInt64();
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
