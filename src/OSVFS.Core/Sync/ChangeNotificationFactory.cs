using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using OSVFS.Sync.AzureQueue;
using OSVFS.Sync.Sqs;

namespace OSVFS.Sync;

/// <summary>
/// Provider-aware dispatch for the push-mode (<c>change-source = "events"</c>)
/// change source. Centralizing the switch here keeps the host's change-source
/// selection a single arm per call site and gives Phase 2 a single seam for
/// adding Azure Event Grid → Storage Queue and GCS Pub/Sub implementations
/// alongside the existing S3 + SQS path.
/// </summary>
internal static class ChangeNotificationFactory
{
    /// <summary>
    /// Builds the push-mode <see cref="IChangeSource"/> for
    /// <paramref name="provider"/>. The interpretation of
    /// <paramref name="queueOrSubscription"/> is provider-specific:
    /// <list type="bullet">
    /// <item>S3 → SQS queue URL or queue name carrying EventBridge S3 notifications.</item>
    /// <item>Azure Blob → Azure Storage Queue URL fed by an Event Grid subscription (planned).</item>
    /// <item>GCS → Pub/Sub subscription resource name (planned).</item>
    /// </list>
    /// Providers whose backend is not yet implemented throw
    /// <see cref="NotSupportedException"/> so the operator hits a clear
    /// "use polling for now" message at startup rather than a downstream wiring failure.
    /// </summary>
    public static IChangeSource Create(
        ObjectStoreProvider provider,
        string queueOrSubscription,
        string bucketName,
        string? keyPrefix,
        string? endpointUrl,
        string? region,
        IObjectStoreCredentialSource? credentials,
        ILoggerFactory loggerFactory) =>
        provider switch
        {
            ObjectStoreProvider.S3 => SqsChangeSourceFactory.Create(
                queueOrSubscription,
                bucketName,
                keyPrefix,
                endpointUrl,
                region,
                credentials,
                loggerFactory.CreateLogger<SqsChangeSource>()),
            ObjectStoreProvider.Gcs => throw new NotSupportedException(
                "Push-mode change notifications for GCS (Pub/Sub) are not yet implemented. " +
                "Use 'change-source = \"polling\"' until the GCS backend lands."),
            ObjectStoreProvider.AzureBlob => AzureStorageQueueChangeSourceFactory.Create(
                queueOrSubscription,
                bucketName,
                keyPrefix,
                credentials,
                loggerFactory.CreateLogger<AzureStorageQueueChangeSource>()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider), provider, "Unknown object-store provider."),
        };
}
