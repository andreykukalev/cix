using System.ComponentModel;
using Cix.Domain.Exchange;
using Cix.Domain.Scanning;
using Cix.Engine.Scanning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

/// <summary>The deterministic market scan for agents: ranking, exclusions and suggested entries and exits. Read-only.</summary>
[McpServerToolType]
public sealed class ScanTools(ToolCallContext context, MarketScanService scanner)
{
    [McpServerTool(Name = "scan_market", ReadOnly = true)]
    [Description(
        "Scans the curated universe now. Returns entries (suggested buys with limitPrice, baseSize, stopLoss and takeProfit already "
        + "sized for the risk per trade), exits (held positions at their stop, target or in a downtrend, with the size to sell), "
        + "ranked (symbols passing the liquidity, spread, volatility and cost filters, best first, with noEntryReason) and "
        + "excluded (symbols filtered out, with the reason). Signals are suggestions: the risk gate still decides.")]
    public async Task<string> ScanMarket(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("scan_market", cancellationToken).ConfigureAwait(false);
        ScanResult result;
        try
        {
            result = await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExchangeException or InvalidOperationException or HttpRequestException)
        {
            throw new McpException("The market can't be scanned right now; try again shortly."); // no exchange text reaches agents
        }

        await context.JournalAsync(
            "scan_market",
            $"{result.Entries.Count} entries, {result.Exits.Count} exits, {result.Ranked.Count} ranked, {result.Excluded.Count} excluded",
            null,
            cancellationToken).ConfigureAwait(false);

        return ToolJson.Of(new
        {
            result.AsOf,
            halted = scanner.Halted,
            result.Exits,
            result.Entries,
            result.Ranked,
            excluded = result.Excluded.Select(e => new { e.Symbol, e.Reason, e.Detail }),
        });
    }
}
