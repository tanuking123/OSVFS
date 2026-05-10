using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OSVFS.Sync.AzureQueue;

/// <summary>
/// <see cref="IChangeSource"/> that polls an Azure Storage Queue carrying
/// Event Grid Blob notifications. Successful messages are translated to
/// <see cref="ObjectChangeEvent"/>s and yielded; consumed (or unparseable)
/// messages are deleted from the queue, while transient receive failures back
/// off and retry without deleting so the visibility timeout takes over.
/// </summary>
/// <remarks>
/// <para>
/// Storage Queue does not support long-poll the way SQS does, so the source
/// loops with a fixed <see cref="PollInterval"/> between empty receives. Each
/// receive pulls up to <see cref="MaxMessagesPerReceive"/> messages and holds
/// them invisible for <see cref="VisibilityTimeoutSeconds"/> while we apply
/// them.
/// </para>
/// <para>
/// Self-suppression for events that echo the host's own writes is handled by
/// <see cref="ObjectStoreChangeWatcher"/> via its recent-mutation map; this
/// source is intentionally stateless beyond its queue connection.
/// </para>
/// </remarks>
internal sealed class AzureStorageQueueChangeSource : IChangeSource
{
    /// <summary>
    /// Storage Queue caps a single ReceiveMessages call at 32 messages.
    /// Pulling the maximum keeps round-trip overhead low.
    /// </summary>
    public const int MaxMessagesPerReceive = 32;

    /// <summary>
    /// How long a message stays invisible after we receive it. Long enough
    /// for the watcher to apply the change before Storage Queue would re-deliver.
    /// </summary>
    public const int VisibilityTimeoutSeconds = 30;

    /// <summary>
    /// Delay between empty receives. Storage Queue lacks SQS-style long-poll
    /// so the source waits this long when the queue is idle to keep request
    /// rate (and cost) bounded.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay between failed receive cycles to avoid hot-looping when the
    /// service or network is misbehaving.
    /// </summary>
    private static readonly TimeSpan BackoffOnError = TimeSpan.FromSeconds(5);

    private readonly QueueClient client;
    private readonly string containerName;
    private readonly string keyPrefix;
    private readonly ILogger<AzureStorageQueueChangeSource> logger;

    /// <summary>
    /// Creates a Storage-Queue-backed change source bound to
    /// <paramref name="client"/>. The source filters incoming events to those
    /// whose container matches <paramref name="containerName"/> and whose
    /// blob name falls under <paramref name="keyPrefix"/> (when non-empty).
    /// </summary>
    public AzureStorageQueueChangeSource(
        QueueClient client,
        string containerName,
        string? keyPrefix,
        ILogger<AzureStorageQueueChangeSource> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(containerName);

        this.client = client;
        this.containerName = containerName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectChangeEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation(
            "Azure Storage Queue change source started (queue = {Queue}, container = {Container}).",
            client.Name, containerName);

        while (!ct.IsCancellationRequested)
        {
            QueueMessage[]? messages;
            try
            {
                var resp = await client.ReceiveMessagesAsync(
                    maxMessages: MaxMessagesPerReceive,
                    visibilityTimeout: TimeSpan.FromSeconds(VisibilityTimeoutSeconds),
                    cancellationToken: ct).ConfigureAwait(false);
                messages = resp.Value;
            }
            catch (OperationCanceledException) { yield break; }
            catch (RequestFailedException ex)
            {
                logger.LogError(
                    ex, "Storage Queue ReceiveMessages failed; backing off {Delay}.", BackoffOnError);
                try { await Task.Delay(BackoffOnError, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            if (messages is null || messages.Length == 0)
            {
                try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            foreach (var msg in messages)
            {
                ct.ThrowIfCancellationRequested();

                var converted = TryConvert(msg);
                // Delete before yielding: the queue's at-least-once semantics
                // combined with the watcher's idempotent apply path mean
                // losing a duplicate on consumer cancellation is preferable to
                // leaving the message in the queue when the iterator is
                // suspended at the yield and never resumed.
                await TryDeleteAsync(msg, ct).ConfigureAwait(false);
                if (converted is { } ev)
                {
                    yield return ev;
                }
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Attempts to map a queue message to an <see cref="ObjectChangeEvent"/>.
    /// Returns null when the message is malformed, references a different
    /// container, or falls outside the linked prefix; the caller still
    /// deletes such messages so they don't redeliver indefinitely.
    /// </summary>
    private ObjectChangeEvent? TryConvert(QueueMessage msg)
    {
        var body = msg.MessageText;
        if (string.IsNullOrEmpty(body))
        {
            logger.LogWarning("Dropping empty Storage Queue message {MessageId}.", msg.MessageId);
            return null;
        }

        EventGridBlobEvent? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                body,
                EventGridBlobEventJsonContext.Default.EventGridBlobEvent);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex, "Dropping unparseable Storage Queue message {MessageId}.", msg.MessageId);
            return null;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.EventType))
        {
            logger.LogWarning(
                "Dropping Storage Queue message {MessageId}: missing Event Grid envelope.",
                msg.MessageId);
            return null;
        }

        var kind = envelope.EventType switch
        {
            "Microsoft.Storage.BlobCreated" => ObjectChangeKind.Upserted,
            "Microsoft.Storage.BlobDeleted" => ObjectChangeKind.Deleted,
            _ => (ObjectChangeKind?)null,
        };
        if (kind is null)
        {
            logger.LogWarning(
                "Dropping Storage Queue message {MessageId}: unrecognized eventType {EventType}.",
                msg.MessageId, envelope.EventType);
            return null;
        }

        // Subject format:
        //   /blobServices/default/containers/{container}/blobs/{blobname}
        // The blob name segment is everything after the literal "/blobs/".
        var (eventContainer, blobKey) = ParseSubject(envelope.Subject);
        if (eventContainer is null || blobKey is null)
        {
            logger.LogWarning(
                "Dropping Storage Queue message {MessageId}: subject {Subject} did not match the expected blob path.",
                msg.MessageId, envelope.Subject);
            return null;
        }

        if (!string.Equals(eventContainer, containerName, StringComparison.Ordinal))
        {
            return null; // event for a different container — silently ignore
        }

        if (keyPrefix.Length > 0 && !blobKey.StartsWith(keyPrefix, StringComparison.Ordinal))
        {
            return null; // event for a blob outside the linked prefix — silently ignore
        }

        var relKey = KeyPath.StripPrefix(keyPrefix, blobKey);
        return new ObjectChangeEvent(
            Kind: kind.Value,
            Key: relKey,
            RelativePath: KeyPath.ToRelativePath(relKey),
            Size: envelope.Data?.ContentLength ?? 0,
            LastModified: default,
            ETag: envelope.Data?.ETag ?? string.Empty);
    }

    /// <summary>
    /// Pulls (container, blobName) out of an Event Grid <c>subject</c> field.
    /// Returns (null, null) when the subject does not match the expected
    /// <c>/blobServices/default/containers/{container}/blobs/{name}</c> shape.
    /// </summary>
    internal static (string? Container, string? BlobName) ParseSubject(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return (null, null);
        const string ContainersPrefix = "/blobServices/default/containers/";
        const string BlobsMarker = "/blobs/";
        if (!subject.StartsWith(ContainersPrefix, StringComparison.Ordinal)) return (null, null);
        var afterContainers = subject[ContainersPrefix.Length..];
        var blobsIdx = afterContainers.IndexOf(BlobsMarker, StringComparison.Ordinal);
        if (blobsIdx < 0) return (null, null);
        var container = afterContainers[..blobsIdx];
        var blobName = afterContainers[(blobsIdx + BlobsMarker.Length)..];
        if (container.Length == 0 || blobName.Length == 0) return (null, null);
        return (container, blobName);
    }

    /// <summary>
    /// Deletes a processed message. Failures are logged but do not break the
    /// loop — the message will redeliver after the visibility timeout, which
    /// the watcher's idempotent apply path tolerates.
    /// </summary>
    private async Task TryDeleteAsync(QueueMessage msg, CancellationToken ct)
    {
        try
        {
            await client.DeleteMessageAsync(msg.MessageId, msg.PopReceipt, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown — no-op */ }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(
                ex, "Failed to delete Storage Queue message {MessageId}; will redeliver after visibility timeout.",
                msg.MessageId);
        }
    }
}
