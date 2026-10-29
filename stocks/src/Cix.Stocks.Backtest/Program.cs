using System.Globalization;
using System.Text;
using System.Text.Json;
using Cix.Domain.Exchange;
using Cix.Stocks;
using Cix.Stocks.MarketData;
using Cix.Stocks.Simulation;

// Backtest of the stocks advisor on EODHD daily bars (ADR-0023). The key comes from EODHD_API_TOKEN (user secrets or the
// environment, never an argument, so it stays out of shell history); without it, EODHD's public demo key, which covers
// a handful of US tickers. Example:
//   dotnet run --project stocks/src/Cix.Stocks.Backtest -- --symbols AAPL.US,MSFT.US,CSPX.LSE:USD --benchmark VTI.US \
//     --from 2016-01-01 --to 2026-10-28 --capital 10000 --currency EUR --fx USD=0.92 --out backtest-out
return await BacktestCommand.RunAsync(args);

internal static class BacktestCommand
{
    private static readonly Dictionary<string, string> CurrencyByExchange = new(StringComparer.Ordinal)
    {
        ["US"] = "USD", ["XETRA"] = "EUR", ["F"] = "EUR", ["PA"] = "EUR", ["AS"] = "EUR", ["MI"] = "EUR", ["MC"] = "EUR",
        ["BR"] = "EUR", ["LS"] = "EUR", ["VI"] = "EUR", ["HE"] = "EUR", ["SW"] = "CHF",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        Dictionary<string, string> options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return await RunAsync(options, CancellationToken.None);
        }
        catch (Exception e) when (e is ArgumentException or StockDataException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const string Usage = """
        Usage: --symbols A.US,B.LSE:USD --benchmark VTI.US --from YYYY-MM-DD --to YYYY-MM-DD
               [--capital 10000] [--currency EUR] [--fx USD=0.92,GBP=1.17] [--step 1] [--out backtest-out]
               [--cache .cache/eodhd] [--commission 0.0025] [--min-commission 1] [--fx-fee 0.0025] [--slippage 0.001]
        A symbol's currency comes from its exchange (US: USD, XETRA/PA/AS...: EUR); give it after a colon otherwise.
        The key is read from EODHD_API_TOKEN; without it the public demo key is used (a few US tickers only).
        """;

    private static async Task<int> RunAsync(Dictionary<string, string> o, CancellationToken cancellationToken)
    {
        var from = DateOnly.ParseExact(Required(o, "from"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact(Required(o, "to"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var baseCurrency = o.GetValueOrDefault("currency", "USD");
        var capital = Decimal(o, "capital", 10_000m);
        var step = Decimal(o, "step", 1m);
        var fx = (o.GetValueOrDefault("fx") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => p[0].Trim(), p => decimal.Parse(p[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var instruments = Required(o, "symbols").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Instrument(s.Trim(), step)).ToList();
        var benchmarkSymbol = Required(o, "benchmark");
        var simulation = new SimulationSettings
        {
            CommissionFraction = Decimal(o, "commission", 0.0025m),
            MinCommission = Decimal(o, "min-commission", 1m),
            FxFeeFraction = Decimal(o, "fx-fee", 0.0025m),
            SlippageFraction = Decimal(o, "slippage", 0.001m),
        }.Validate();
        var advisor = new AdvisorSettings { BenchmarkSymbol = benchmarkSymbol }.Validate();

        var token = Environment.GetEnvironmentVariable("EODHD_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("EODHD_API_TOKEN isn't set: using EODHD's public demo key (a few US tickers only).");
            token = "demo";
        }

        // Enough history before the start for the slowest indicator (200 trading days) and the momentum window.
        var loadFrom = from.AddDays(-500);
        using var http = new HttpClient { BaseAddress = EodhdClient.DefaultBaseAddress, Timeout = TimeSpan.FromSeconds(60) };
        var source = new CachedBarSource(new EodhdClient(http, token), o.GetValueOrDefault("cache", Path.Combine(".cache", "eodhd")));

        var watchlist = new List<PriceHistory>();
        foreach (var instrument in instruments)
            watchlist.Add(new PriceHistory(instrument, await source.GetDailyBarsAsync(instrument.Symbol, loadFrom, to, cancellationToken)));
        var benchmark = await source.GetDailyBarsAsync(benchmarkSymbol, loadFrom, to, cancellationToken);

        var result = Backtester.Run(watchlist, benchmark, from, to, baseCurrency, capital, fx, advisor, simulation);

        var outDir = o.GetValueOrDefault("out", "backtest-out");
        Directory.CreateDirectory(outDir);
        await WriteAsync(outDir, result, o, cancellationToken);

        Console.WriteLine($"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}, {capital.ToString(CultureInfo.InvariantCulture)} {baseCurrency}, {instruments.Count} instruments, benchmark {benchmarkSymbol}");
        Console.WriteLine(Line("Strategy", result.Strategy));
        Console.WriteLine(Line("Benchmark (buy and hold)", result.Benchmark));
        Console.WriteLine($"Report: {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static string Line(string name, BacktestStats s)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{name,-26} return {s.TotalReturn:P1}, CAGR {s.Cagr:P1}, max drawdown {s.MaxDrawdown:P1}, in market {s.TimeInMarket:P0}");
        return s.RoundTrips > 0 ? line + string.Create(CultureInfo.InvariantCulture, $", {s.RoundTrips} round trips, {s.WinRate:P0} won") : line;
    }

    private static async Task WriteAsync(string dir, BacktestResult result, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        var summary = new { Options = options, result.Strategy, result.Benchmark, Skipped = result.Skipped.GroupBy(s => s.Reason).ToDictionary(g => g.Key, g => g.Count()) };
        await File.WriteAllTextAsync(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, Indented), cancellationToken);

        var equity = new StringBuilder("day,equity,benchmark,positions\n");
        foreach (var p in result.Equity)
            equity.Append(CultureInfo.InvariantCulture, $"{p.Day:yyyy-MM-dd},{Math.Round(p.Equity, 2)},{Math.Round(p.Benchmark, 2)},{p.Positions}\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "equity.csv"), equity.ToString(), cancellationToken);

        var trades = new StringBuilder("day,symbol,kind,quantity,price,fees,profit,reason\n");
        foreach (var t in result.Trades)
            trades.Append(CultureInfo.InvariantCulture, $"{t.Day:yyyy-MM-dd},{t.Symbol},{t.Kind},{t.Quantity},{Math.Round(t.Price, 4)},{Math.Round(t.FeesBase, 2)},{(t.ProfitBase is { } p ? Math.Round(p, 2) : "")},{t.Reason}\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "trades.csv"), trades.ToString(), cancellationToken);
    }

    private static Instrument Instrument(string spec, decimal step)
    {
        var parts = spec.Split(':', 2);
        var symbol = parts[0];
        if (!StockSymbol.IsValid(symbol))
            throw new ArgumentException($"'{symbol}' isn't TICKER.EXCHANGE.");
        var exchange = symbol[(symbol.LastIndexOf('.') + 1)..];
        var currency = parts.Length == 2 ? parts[1] : CurrencyByExchange.GetValueOrDefault(exchange)
            ?? throw new ArgumentException($"Give {symbol}'s currency after a colon, for example {symbol}:USD (LSE lines trade in USD, GBP or GBX).");
        return new Instrument(symbol, symbol, currency, InstrumentKind.Stock, QuantityStep: step);
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Expected --name value, got '{args[i]}'.");
            options[args[i][2..]] = args[i + 1];
        }

        return options;
    }

    private static string Required(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

    private static decimal Decimal(Dictionary<string, string> o, string name, decimal fallback) =>
        o.TryGetValue(name, out var value) ? decimal.Parse(value, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>Daily bars cached on disk per symbol and range, so repeated runs don't spend the provider's daily calls.</summary>
internal sealed class CachedBarSource(IDailyBarSource inner, string directory) : IDailyBarSource
{
    public async Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string symbol, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        if (!StockSymbol.IsValid(symbol))
            throw new ArgumentException("The symbol must be TICKER.EXCHANGE, for example AAPL.US.", nameof(symbol));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{symbol}_{firstDay:yyyyMMdd}_{lastDay:yyyyMMdd}.json");
        if (File.Exists(path))
        {
            await using var cached = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<Candle>>(cached, cancellationToken: cancellationToken) ?? [];
        }

        var bars = await inner.GetDailyBarsAsync(symbol, firstDay, lastDay, cancellationToken);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(bars), cancellationToken);
        return bars;
    }
}
