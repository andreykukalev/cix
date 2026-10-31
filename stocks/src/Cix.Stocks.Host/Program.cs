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
        using var http = new HttpClient { BaseAddress = EodhdClient.DefaultBaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        var source = new EodhdClient(http, key);
        try
        {
            if (dryRun)
            {
                var memory = new InMemoryStocksStore();
                var report = await new DailyRun(config, source, memory, TimeProvider.System).RunAsync(cancel.Token);
                Print(report);
                if (memory.Documents.TryGetValue("advice/advice-latest", out var advice))
                    Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(advice), Indented));
                return 0;
            }

            var endpoint = Env("STOCKS_COSMOS_ENDPOINT") ?? throw new ArgumentException("STOCKS_COSMOS_ENDPOINT isn't set.");
            var clientId = Env("AZURE_CLIENT_ID") ?? throw new ArgumentException("AZURE_CLIENT_ID (the job's managed identity) isn't set.");
            using var store = CosmosStocksStore.WithManagedIdentity(new Uri(endpoint), Env("STOCKS_COSMOS_DATABASE") ?? CosmosStocksStore.DefaultDatabase, clientId);
            Print(await new DailyRun(config, source, store, TimeProvider.System).RunAsync(cancel.Token));
            return 0;
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"Configuration: {e.Message}");
            return 2;
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

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? ReadKey()
    {
        var file = Env("EODHD_API_TOKEN_FILE") ?? DefaultKeyFile;
        if (File.Exists(file))
            return File.ReadAllText(file).Trim() is { Length: > 0 } fromFile ? fromFile : null;
        return Env("EODHD_API_TOKEN")?.Trim();
    }
}
