using System.Net;
using System.Text.Json;
using Cix.Domain.Exchange;

namespace Cix.Exchange.RevolutX.Tests;

public sealed class PublicMarketDataTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1792677504427);

    private static (RevolutXPublicMarketData Client, FakeHandler Handler) Create()
    {
        var handler = new FakeHandler();
        var client = new RevolutXPublicMarketData(new HttpClient(handler), new RevolutXOptions(), new FixedTime(Now), (_, _) => Task.CompletedTask);
        return (client, handler);
    }

    [Fact]
    public async Task GetQuoteAsync_PicksBestLevels_AndSendsNoCredentials()
    {
        var (client, handler) = Create();
        handler.RespondFixture("order_book.json");

        var quote = await client.GetQuoteAsync("btc/eur", CancellationToken.None);

        Assert.Equal(new MarketQuote("BTC-EUR", 72939.90m, 0.03m, 72941.50m, 0.012m, DateTimeOffset.FromUnixTimeMilliseconds(1792677504000)), quote);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://revx.revolut.com/api/2.0/public/order-book/BTC-EUR?limit=5&region=EEA", request.Uri.ToString());
        Assert.DoesNotContain(RevolutXAuth.ApiKeyHeader, request.Headers.Keys);
        Assert.DoesNotContain(RevolutXAuth.SignatureHeader, request.Headers.Keys);
    }

    [Fact]
    public void ParseOrderBookTop_ExchangeClockAhead_UsesReceiveTime()
    {
        using var doc = JsonDocument.Parse(Fixture.Read("order_book.json"));
        var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(1792677503000); // earlier than metadata.timestamp

        var quote = RevolutXParser.ParseOrderBookTop(doc.RootElement, "BTC-EUR", receivedAt);

        Assert.Equal(receivedAt, quote.AsOf);
    }

    [Fact]
    public void ParseOrderBookTop_NoTimestamp_UsesReceiveTime()
    {
        using var doc = JsonDocument.Parse("""{"data":{"bids":[{"price":"1","quantity":"2"}],"asks":[{"price":"3","quantity":"4"}]}}""");

        var quote = RevolutXParser.ParseOrderBookTop(doc.RootElement, "BTC-EUR", Now);

        Assert.Equal(Now, quote.AsOf);
        Assert.Equal(2m, quote.Mid);
    }

    [Fact]
    public void ParseOrderBookTop_EmptySide_Throws()
    {
        using var doc = JsonDocument.Parse("""{"data":{"bids":[],"asks":[{"price":"3","quantity":"4"}]}}""");

        Assert.Throws<FormatException>(() => RevolutXParser.ParseOrderBookTop(doc.RootElement, "BTC-EUR", Now));
    }

    [Theory]
    [InlineData("0", "3")]      // zero bid
    [InlineData("5", "3")]      // crossed book
    [InlineData("-1", "3")]     // negative price
    public void ParseOrderBookTop_InvalidTop_Throws(string bid, string ask)
    {
        using var doc = JsonDocument.Parse($$$"""{"data":{"bids":[{"price":"{{{bid}}}","quantity":"1"}],"asks":[{"price":"{{{ask}}}","quantity":"1"}]}}""");

        Assert.Throws<FormatException>(() => RevolutXParser.ParseOrderBookTop(doc.RootElement, "BTC-EUR", Now));
    }

    [Fact]
    public async Task GetQuoteAsync_ServerError_Throws()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.BadRequest, Fixture.Read("error_400.json"));

        await Assert.ThrowsAsync<ExchangeException>(() => client.GetQuoteAsync("BTC-EUR", CancellationToken.None));
    }
}
