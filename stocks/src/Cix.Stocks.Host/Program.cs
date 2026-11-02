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
// The stocks analyst job (ADR-0027) runs the same image with one of:
//   --brief <file>                 writes the brief for the latest advice (news included) to the file; exit 3 when the
//                                  analyst already answered for that close, or there is no advice yet
//   --accept <brief> <answer>      checks Claude's answer against the brief and saves the analysis
//   --not-run <brief> <reason>     saves that Claude didn't run for the brief's close (usage-limit, credentials, timeout,
//                                  failed); a done analysis for that close is never replaced
// Exit codes: 0 advised or already advised, 1 failed, 2 bad configuration, 3 nothing to analyse.
return await StocksHost.RunAsync(args);

internal static class StocksHost
{
    private const string DefaultKeyFile = "/run/secrets/eodhd-api-token";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        // The job's replicaTimeout is 15 minutes: the search stops fetching bars after 12 and lists what it found.
        var deadline = TimeProvider.System.GetUtcNow().AddMinutes(12);
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
        var analyst = args is ["--brief", _] or ["--accept", _, _] or ["--not-run", _, _];
        if (args.Length > 0 && !dryRun && !analyst)
        {
            Console.Error.WriteLine("Usage: Cix.Stocks.Host [--dry-run | --brief <file> | --accept <brief> <answer> | --not-run <brief> <reason>]");
            return 2;
        }

        if (args is ["--accept", _, _] or ["--not-run", _, _])
            return await AnalysisAsync(args, cancel.Token);

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
        // The whole-market download is 20 to 40 MB: its own client, with room for it but not for much more (the job has 0.5 GiB).
        using var bulkHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = EodhdClient.DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(180),
            MaxResponseContentBufferSize = 64 * 1024 * 1024,
        };
        var market = new EodhdClient(bulkHttp, key);
        try
        {
            if (dryRun)
            {
                var memory = new InMemoryStocksStore();
                var report = await new DailyRun(config, source, memory, TimeProvider.System).RunAsync(cancel.Token);
                Print(report);
                Print(await new MarketSearch(config, market, source, memory, TimeProvider.System, deadline).RunAsync(cancel.Token));
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
            if (args is ["--brief", var briefFile])
            {
                var brief = await new AnalystBriefing(store, source, TimeProvider.System).BuildAsync(cancel.Token);
                if (brief is null)
                {
                    Console.WriteLine("Analyst: nothing to analyse (no advice yet, or this close was analysed).");
                    return 3;
                }

                await File.WriteAllTextAsync(briefFile, JsonSerializer.Serialize(brief, StocksJson.Options), cancel.Token);
                Console.WriteLine($"Analyst brief for {brief.AsOf:yyyy-MM-dd}: {brief.Holdings.Count} holdings, {brief.Buys.Count} buys, " +
                                  $"{brief.Candidates.Count} candidates, news on {brief.News.Count} ({brief.NewsMissing.Count} missing).");
                return 0;
            }

            Print(await new DailyRun(config, source, store, TimeProvider.System).RunAsync(cancel.Token));
            Print(await new MarketSearch(config, market, source, store, TimeProvider.System, deadline).RunAsync(cancel.Token));
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

    /// <summary>
    /// Saves the analyst's answer (or that Claude didn't run) for the brief's close. Needs the database only, not the EODHD
    /// key. A done analysis is never replaced by a later not-run for the same close.
    /// </summary>
    private static async Task<int> AnalysisAsync(string[] args, CancellationToken cancellationToken)
    {
        AnalystBrief brief;
        try
        {
            brief = JsonSerializer.Deserialize<AnalystBrief>(await File.ReadAllTextAsync(args[1], cancellationToken), StocksJson.Options)
                    ?? throw new JsonException("The brief is empty.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Analyst: the brief can't be read: {e.GetType().Name}.");
            return 2;
        }

        if (Env("STOCKS_COSMOS_ENDPOINT") is not { } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || Env("AZURE_CLIENT_ID") is not { } clientId)
        {
            Console.Error.WriteLine("Configuration: STOCKS_COSMOS_ENDPOINT and AZURE_CLIENT_ID must be set.");
            return 2;
        }

        var now = TimeProvider.System.GetUtcNow();
        AnalysisDocument analysis;
        if (args is ["--not-run", _, var reason])
        {
            if (!AnalysisDocument.NotRunReasons.Contains(reason))
            {
                Console.Error.WriteLine($"Analyst: the reason must be one of {string.Join(", ", AnalysisDocument.NotRunReasons)}.");
                return 2;
            }

            analysis = AnalysisDocument.Missing(brief.AsOf, now, AnalysisDocument.NotRun, reason);
        }
        else
        {
            string answer;
            try
            {
                answer = new FileInfo(args[2]) is { Exists: true, Length: <= AnalystAnswer.MaxBytes } file
                    ? await File.ReadAllTextAsync(file.FullName, cancellationToken) : "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                answer = "";
            }

            analysis = AnalystAnswer.Check(brief, answer, now);
        }

        using var store = CosmosStocksStore.WithManagedIdentity(endpointUri, Env("STOCKS_COSMOS_DATABASE") ?? CosmosStocksStore.DefaultDatabase, clientId);
        if (!await AnalysisRecord.SaveAsync(store, analysis, cancellationToken))
        {
            Console.WriteLine($"Analyst: {analysis.Status} for {analysis.AsOf:yyyy-MM-dd} not saved: a done or later analysis is already saved.");
            return analysis.Status == AnalysisDocument.Done ? 0 : 1;
        }

        Console.WriteLine($"Analyst: {analysis.Status}{(analysis.Reason is { } r ? $" ({r})" : "")} for {analysis.AsOf:yyyy-MM-dd}: " +
                          $"{analysis.Holdings.Count} holdings, {analysis.Picks.Count} picks, {analysis.Skips.Count} skips, {analysis.Dropped} dropped.");
        return analysis.Status == AnalysisDocument.Done ? 0 : 1;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? ReadKey()
    {
        var file = Env("EODHD_API_TOKEN_FILE") ?? DefaultKeyFile;
        if (File.Exists(file))
            return File.ReadAllText(file).Trim() is { Length: > 0 } fromFile ? fromFile : null;
        return Env("EODHD_API_TOKEN")?.Trim();
    }
}
