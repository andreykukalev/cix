using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Queues;

namespace Cix.Engine.AgentRuns;

/// <summary>What a job finds on its queue: which run to claim, and why it was started. Never a secret.</summary>
public sealed record AgentRunMessage(string RunId, string Job, string Purpose);

/// <summary>
/// The agent jobs' Storage queues (ADR-0013), reached with the engine's managed identity, which needs only the
/// "Storage Queue Data Message Sender" role on them. Messages are JSON, Base64-encoded as the Storage tools expect, and
/// expire when their run would be given up on unclaimed, so a job that comes back late finds nothing old to do.
/// </summary>
public sealed class StorageAgentRunQueue : IAgentRunQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<AgentRunKind, QueueClient> _queues;

    public StorageAgentRunQueue(AgentRunQueueOptions options, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        credential ??= new ManagedIdentityCredential(string.IsNullOrEmpty(options.ManagedIdentityClientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        var service = new QueueServiceClient(new Uri(options.ServiceUri!), credential, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        _queues = Enum.GetValues<AgentRunKind>().ToDictionary(kind => kind, kind => service.GetQueueClient(options.QueueFor(kind)));
    }

    /// <summary>The message a job gets for this run.</summary>
    public static string MessageFor(AgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(new AgentRunMessage(run.Id, run.Kind.Role(), run.Purpose), Json);
    }

    public async Task SendAsync(AgentRun run, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _queues[run.Kind].SendMessageAsync(MessageFor(run), visibilityTimeout: null, timeToLive, cancellationToken).ConfigureAwait(false);
    }
}
