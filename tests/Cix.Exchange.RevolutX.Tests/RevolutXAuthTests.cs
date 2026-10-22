using Cix.Exchange.RevolutX;

namespace Cix.Exchange.RevolutX.Tests;

public sealed class RevolutXAuthTests
{
    [Fact]
    public void BuildMessage_DocumentedExample_MatchesExactly()
    {
        // Example from the Revolut X REST API docs ("String to sign").
        const string body = "{\"client_order_id\":\"3b364427-1f4f-4f66-9935-86b6fb115d26\",\"symbol\":\"BTC-USD\",\"side\":\"BUY\",\"order_configuration\":{\"limit\":{\"base_size\":\"0.1\",\"price\":\"90000.1\"}}}";
        const string expected = "1765360896219POST/api/1.0/orders" + body;

        var message = RevolutXAuth.BuildMessage(1765360896219, "post", "/api/1.0/orders", null, body);

        Assert.Equal(expected, message);
    }

    [Fact]
    public void BuildMessage_QueryWithLeadingQuestionMark_StripsIt()
    {
        var message = RevolutXAuth.BuildMessage(1, "GET", "/api/1.0/candles/BTC-EUR", "?interval=60", null);

        Assert.Equal("1GET/api/1.0/candles/BTC-EURinterval=60", message);
    }

    [Fact]
    public void BuildMessage_PathNotUnderApi_Throws()
    {
        Assert.Throws<ArgumentException>(() => RevolutXAuth.BuildMessage(1, "GET", "/1.0/balances", null, null));
    }

    [Fact]
    public void CreateHeaders_UsesTimestampAndBase64Signature()
    {
        var time = new FixedTime(DateTimeOffset.FromUnixTimeMilliseconds(1765360896219));
        var signer = new RecordingSigner();
        var auth = new RevolutXAuth("test-key", signer, time);

        var headers = auth.CreateHeaders("GET", "/api/1.0/balances", null, null).ToDictionary(h => h.Key, h => h.Value);

        Assert.Equal("test-key", headers[RevolutXAuth.ApiKeyHeader]);
        Assert.Equal("1765360896219", headers[RevolutXAuth.TimestampHeader]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), headers[RevolutXAuth.SignatureHeader]);
        Assert.Equal("1765360896219GET/api/1.0/balances", signer.LastMessage);
    }

    private sealed class RecordingSigner : IEd25519Signer
    {
        public string? LastMessage { get; private set; }

        public byte[] Sign(ReadOnlySpan<byte> message)
        {
            LastMessage = System.Text.Encoding.UTF8.GetString(message);
            return [1, 2, 3];
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
