using System.Runtime.InteropServices;
using System.Text.Json;
using Cix.Stocks;
using Cix.Stocks.Host;
using Cix.Stocks.MarketData;

// The stocks advisor's daily run (ADR-0023), started by a Container Apps job after the US close. Settings come from
// config/stocks.json; the environment gives the rest, never as arguments:
//   EODHD key      the file EODHD_API_TOKEN_FILE (default /run/secrets/eodhd-api-token, mounted from Key Vault), or
//                  EODHD_API_TOKEN for a local run
//   database       STOCKS_COSMOS_ENDPOINT, STOCKS_COSMOS_DATABASE (default cix-stocks), AZURE_CLIENT_ID (managed identity)
// After the advice, the market-wide search (ADR-0025) runs when config/stocks.json turns it on: its failure fails the run,
// but the advice is saved by then.
// `--dry-run` keeps everything in memory and prints the advice instead: it still spends EODHD calls.
// Exit codes: 0 advised or already advised, 1 failed, 2 bad configuration.
return await StocksHost.RunAsync(args);

internal static class StocksHost
{
    private const string DefaultKeyFile = "/run/secrets/eodhd-api-token";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        // The job's stop signal: cancel the run instead of the process dying mid-write.
        using var stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancel.Cancel();
        });

        var dryRun = args is ["--dry-run"];
        if (args.Length > 0 && !dryRun)
        {
            Console.Error.WriteLine("Usage: Cix.Stocks.Host [--dry-run]");
            return 2;
        }

        StocksConfig config;
        string key;
        try
        {
            config = StocksConfig.Load(Env("STOCKS_CONFIG") ?? Path.Combine(AppContext.BaseDirectory, "config", "stocks.json"));
            key = ReadKey() ?? throw new ArgumentException("No EODHD key: mount it at /run/secrets/eodhd-api-token or set EODHD_API_TOKEN.");
        }
        catch (Exception e) when (e is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Configuration: {e.Message}");
            return 2;
        }

        // No request logging on this client: the key travels in the URL.
        // Redirects are refused (the key would follow them), and a year of bars is about 40 KB: 4 MB is plenty.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = EodhdClient.DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
        var source = new EodhdClient(http, key);
        // The whole-market download is tens of megabytes: its own client, with room for it.
        using var bulkHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = EodhdClient.DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(180),
            MaxResponseContentBufferSize = 128 * 1024 * 1024,
        };
        var market = new EodhdClient(bulkHttp, key);
        try
        {
            if (dryRun)
            {
                var memory = new InMemoryStocksStore();
                var report = await new DailyRun(config, source, memory, TimeProvider.System).RunAsync(cancel.Token);
                Print(report);
                Print(await new MarketSearch(config, market, source, memory, TimeProvider.System).RunAsync(cancel.Token));
                foreach (var id in new[] { "advice/advice-latest", "advice/candidates-latest" })
                {
                    if (memory.Documents.TryGetValue(id, out var page))
                        Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(page), Indented));
                }

                return 0;
            }

            if (Env("STOCKS_COSMOS_ENDPOINT") is not { } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                Console.Error.WriteLine("Configuration: STOCKS_COSMOS_ENDPOINT isn't set to a URL.");
                return 2;
            }

            if (Env("AZURE_CLIENT_ID") is not { } clientId)
            {
                Console.Error.WriteLine("Configuration: AZURE_CLIENT_ID (the job's managed identity) isn't set.");
                return 2;
            }

            using var store = CosmosStocksStore.WithManagedIdentity(endpointUri, Env("STOCKS_COSMOS_DATABASE") ?? CosmosStocksStore.DefaultDatabase, clientId);
            Print(await new DailyRun(config, source, store, TimeProvider.System).RunAsync(cancel.Token));
            Print(await new MarketSearch(config, market, source, store, TimeProvider.System).RunAsync(cancel.Token));
            return 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("Stopped before the run finished: nothing after the last saved step was written.");
            return 1;
        }
        catch (Exception e)
        {
            // The type and message only: the EODHD client's messages never carry the URL, and nothing here prints the key.
            Console.Error.WriteLine($"The run failed: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static void Print(RunReport r) =>
        Console.WriteLine($"{r.Outcome}: close {r.AsOf?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, " +
                          $"{r.Calls} EODHD calls, {r.Trades} paper trades, {r.Actions} actions, {r.Notes} notes.");

    private static void Print(CandidatesDocument? c) =>
        Console.WriteLine(c is null ? "Market search: nothing new to search."
            : $"Market search: {c.Status}, {c.Candidates.Count} candidates from {c.Shortlisted} shortlisted of {c.Listed} listed.");

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? ReadKey()
    {
        var file = Env("EODHD_API_TOKEN_FILE") ?? DefaultKeyFile;
        if (File.Exists(file))
            return File.ReadAllText(file).Trim() is { Length: > 0 } fromFile ? fromFile : null;
        return Env("EODHD_API_TOKEN")?.Trim();
    }
}
