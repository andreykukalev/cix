using System.Net;
using System.Text;

namespace Cix.Stocks.MarketData.Tests;

public class EodhdClientTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 10, 28);

    // Built at run time so nothing in the repository looks like a key; the + and / check the escaping.
    private static readonly string Token = string.Concat("test", "+tok/", "en");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static (EodhdClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = EodhdClient.DefaultBaseAddress };
        return (new EodhdClient(http, Token), handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task ReadsARealResponse()
    {
        // EODHD's answer for AAPL.US, 2026-09-01 to 2026-10-28 (the public demo key).
        var (client, _) = Client(_ => Json(Fixture("eodhd-eod-aapl.json")));

        var bars = await client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None);

        Assert.Equal(41, bars.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), bars[0].Start);
        Assert.Equal(325.13m, bars[0].Close);
        Assert.Equal(53167388m, bars[0].Volume);
        Assert.Equal(new DateOnly(2026, 10, 28), TrendIndicators.TradingDay(bars[^1]));
        Assert.Equal(343.83m, bars[^1].Close);
        Assert.True(bars.Zip(bars.Skip(1)).All(p => p.First.Start < p.Second.Start));
    }

    [Fact]
    public async Task AsksForDailyJsonBarsInTheRangeWithTheKeyEscaped()
    {
        var (client, handler) = Client(_ => Json("[]"));

        await client.GetDailyBarsAsync("SAP.XETRA", From, To, CancellationToken.None);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/eod/SAP.XETRA", uri.AbsolutePath);
        Assert.Equal("?from=2026-09-01&to=2026-10-28&period=d&fmt=json&api_token=test%2Btok%2Fen", uri.Query);
    }

    [Fact]
    public async Task AdjustsForSplitsAndDividends()
    {
        // A 4:1 split after this bar: EODHD reports the raw close 400 and the adjusted close 100.
        var (client, _) = Client(_ => Json("""
            [{"date":"2026-09-01","open":380,"high":420,"low":360,"close":400,"adjusted_close":100,"volume":10}]
            """));

        var bar = Assert.Single(await client.GetDailyBarsAsync("XYZ.US", From, To, CancellationToken.None));

        Assert.Equal((95m, 105m, 90m, 100m), (bar.Open, bar.High, bar.Low, bar.Close));
    }

    [Fact]
    public async Task SkipsUnusableRowsAndKeepsTheRangeSortedWithoutDuplicates()
    {
        var (client, _) = Client(_ => Json("""
            [
              {"date":"2026-09-03","open":3,"high":3,"low":3,"close":3,"adjusted_close":3,"volume":1},
              {"date":"2026-09-02","open":2,"high":2,"low":2,"close":2,"adjusted_close":2,"volume":1},
              {"date":"2026-09-02","open":9,"high":9,"low":9,"close":9,"adjusted_close":9,"volume":1},
              {"date":"2026-08-31","open":1,"high":1,"low":1,"close":1,"adjusted_close":1,"volume":1},
              {"date":"2026-09-04","open":0,"high":4,"low":4,"close":4,"adjusted_close":4,"volume":1},
              {"date":"2026-09-05","open":5,"high":4,"low":6,"close":5,"adjusted_close":5,"volume":1},
              {"date":"2026-09-06","open":6,"high":6,"low":6,"close":null,"adjusted_close":6,"volume":1},
              {"date":"09/07/2026","open":7,"high":7,"low":7,"close":7,"adjusted_close":7,"volume":1},
              {"date":"2026-09-08","open":"8","high":"8","low":"8","close":"8","adjusted_close":"8","volume":null}
            ]
            """));

        var bars = await client.GetDailyBarsAsync("XYZ.US", From, To, CancellationToken.None);

        Assert.Equal([2m, 3m, 8m], bars.Select(b => b.Close));
        Assert.Equal(0m, bars[^1].Volume);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthenticated")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "Ticker Not Found.")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    public async Task AnErrorStatusNamesTheSymbolAndStatusOnly(HttpStatusCode status, string body)
    {
        var (client, _) = Client(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        var e = await Assert.ThrowsAsync<StockDataException>(() => client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None));

        Assert.Equal((int)status, e.StatusCode);
        Assert.Contains("AAPL.US", e.Message);
        Assert.DoesNotContain("tok", e.Message);
        Assert.DoesNotContain("api_token", e.Message);
    }

    [Theory]
    [InlineData("Forbidden")]
    [InlineData("{\"error\":\"x\"}")]
    [InlineData("null")]
    public async Task AnAnswerThatIsntAListOfBarsIsAnError(string body)
    {
        var (client, _) = Client(_ => Json(body));

        await Assert.ThrowsAsync<StockDataException>(() => client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None));
    }

    [Fact]
    public async Task ANetworkFailureDoesntLeakTheUrl()
    {
        var (client, _) = Client(r => throw new HttpRequestException($"failed: {r.RequestUri}", null, HttpStatusCode.BadGateway));

        var e = await Assert.ThrowsAsync<StockDataException>(() => client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None));

        Assert.DoesNotContain("tok", e.Message);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task CancellationIsNotTurnedIntoADataError()
    {
        var (client, _) = Client(_ => Json("[]"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDailyBarsAsync("AAPL.US", From, To, cancelled.Token));
    }

    [Theory]
    [InlineData("AAPL")]
    [InlineData("../user.US")]
    [InlineData("AAPL.US?fmt=csv")]
    public async Task ABadSymbolIsRefusedBeforeAnyRequest(string symbol)
    {
        var (client, handler) = Client(_ => Json("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetDailyBarsAsync(symbol, From, To, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NeedsAKeyAndABaseAddress()
    {
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient { BaseAddress = EodhdClient.DefaultBaseAddress }, " "));
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient(), Token));
    }
}
