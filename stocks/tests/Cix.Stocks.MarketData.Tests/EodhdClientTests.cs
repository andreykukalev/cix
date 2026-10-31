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
    public async Task AnAbsurdPriceIsDropped()
    {
        var (client, _) = Client(_ => Json("""
            [{"date":"2026-09-01","open":1,"high":2,"low":1,"close":1,"adjusted_close":1,"volume":10},
             {"date":"2026-09-02","open":1,"high":20000000,"low":1,"close":1,"volume":10}]
            """));

        var bar = Assert.Single(await client.GetDailyBarsAsync("XYZ.US", From, To, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), bar.Start);
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
    public async Task ABodyCutOffMidwayIsADataError()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) });

        var e = await Assert.ThrowsAsync<StockDataException>(() => client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None));

        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task AnAbsurdAdjustmentIsSkipped()
    {
        var (client, _) = Client(_ => Json("""
            [{"date":"2026-09-01","open":1e20,"high":1e20,"low":1e20,"close":1e-20,"adjusted_close":1e20,"volume":1},
             {"date":"2026-09-02","open":2,"high":2,"low":2,"close":2,"adjusted_close":2,"volume":1}]
            """));

        var bars = await client.GetDailyBarsAsync("AAPL.US", From, To, CancellationToken.None);

        Assert.Equal(2m, Assert.Single(bars).Close);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    [InlineData("AAPL.US\n")]
    public async Task ABadSymbolIsRefusedBeforeAnyRequest(string symbol)
    {
        var (client, handler) = Client(_ => Json("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetDailyBarsAsync(symbol, From, To, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NeedsAKeyAndAnHttpsBaseAddress()
    {
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient { BaseAddress = EodhdClient.DefaultBaseAddress }, " "));
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient(), Token));
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient { BaseAddress = new Uri("http://eodhd.com/api/") }, Token));
        Assert.Throws<ArgumentException>(() => new EodhdClient(new HttpClient { BaseAddress = new Uri("https://eodhd.com/api") }, Token));
    }

    // The shape of EODHD's documented answer (eodhd.com/financial-apis/bulk-download-api, filter=extended), trimmed.
    private const string Bulk = """
        [
          {"code":"AAPL","exchange_short_name":"US","date":"2026-10-30","open":270.1,"high":272.3,"low":268.9,"close":271.4,"adjusted_close":271.4,
           "volume":51234567,"name":"Apple Inc","type":"Common Stock","MarketCapitalization":4010000000000,"Beta":1.2,"ema_50d":255.2,
           "ema_200d":231.7,"hi_250d":275.0,"lo_250d":170.1,"avgvol_14d":50000000,"avgvol_50d":48000000,"avgvol_200d":52000000},
          {"code":"ZERO","exchange_short_name":"US","date":"2026-10-30","close":0,"adjusted_close":0,"volume":0},
          {"code":"NOAVG","exchange_short_name":"US","date":"2026-10-30","close":"12.5","adjusted_close":"12.5","volume":"1000",
           "MarketCapitalization":0,"ema_50d":0,"ema_200d":null,"hi_250d":0,"avgvol_50d":0},
          {"code":"bad code!","date":"2026-10-30","close":5},
          {"code":"NODATE","date":"yesterday","close":5}
        ]
        """;

    [Fact]
    public async Task ReadsTheWholeMarketsLastSession()
    {
        var (client, handler) = Client(_ => Json(Bulk));

        var rows = await client.GetMarketDayAsync("US", CancellationToken.None);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/eod-bulk-last-day/US", uri.AbsolutePath);
        Assert.Contains("filter=extended", uri.Query, StringComparison.Ordinal);
        Assert.Contains("fmt=json", uri.Query, StringComparison.Ordinal);
        Assert.Contains($"api_token={Uri.EscapeDataString(Token)}", uri.Query, StringComparison.Ordinal);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new MarketRow("AAPL.US", new DateOnly(2026, 10, 30), 271.4m, 51234567m, 4010000000000m, 255.2m, 231.7m, 275.0m, 48000000m), rows[0]);
        // Zeros and nulls are gaps, not figures.
        Assert.Equal(new MarketRow("NOAVG.US", new DateOnly(2026, 10, 30), 12.5m, 1000m, null, null, null, null, null), rows[1]);
    }

    [Fact]
    public async Task ReadsTheSymbolListOfCommonStocks()
    {
        var (client, handler) = Client(_ => Json("""
            [{"Code":"AAPL","Name":"Apple Inc","Country":"USA","Exchange":"NASDAQ","Currency":"USD","Type":"Common Stock","Isin":"US0378331005"},
             {"Code":"IBM","Name":"International Business Machines\u0007","Country":"USA","Exchange":"NYSE","Currency":"USD","Type":"Common Stock","Isin":null},
             {"Code":"no good","Name":"x","Exchange":"NYSE","Type":"Common Stock"}]
            """));

        var listed = await client.GetListedStocksAsync("US", CancellationToken.None);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/exchange-symbol-list/US", uri.AbsolutePath);
        Assert.Contains("type=common_stock", uri.Query, StringComparison.Ordinal);
        Assert.Equal([new ListedStock("AAPL.US", "Apple Inc", "NASDAQ", "Common Stock"), new ListedStock("IBM.US", "International Business Machines", "NYSE", "Common Stock")], listed);
    }

    [Fact]
    public async Task AFreeKeysRefusalOfTheDownloadNamesTheStatusNotTheUrl()
    {
        var (client, _) = Client(_ => Json("""{"error":"plan"}""", HttpStatusCode.Forbidden));

        var e = await Assert.ThrowsAsync<StockDataException>(() => client.GetMarketDayAsync("US", CancellationToken.None));

        Assert.Equal(403, e.StatusCode);
        Assert.DoesNotContain(Token, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api_token", e.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMarketDayAsync("us/../x", CancellationToken.None));
    }

    [Fact]
    public void ProviderTextLosesFormatCharactersAndIsNeverCutInsideAPair()
    {
        Assert.Equal("Apple Inc", EodhdClient.Clean("Ap\u200Bple\u202E Inc\u2028"));
        Assert.Null(EodhdClient.Clean("\u200E\u0007 "));
        var emoji = new string('a', 79) + "\U0001F600";
        Assert.Equal(new string('a', 79), EodhdClient.Clean(emoji));
    }
}
