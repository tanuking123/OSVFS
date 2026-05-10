using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.AzureBlob;
using OSVFS.Sync;
using OSVFS.Sync.AzureQueue;
using OSVFS.Sync.Sqs;
using Xunit;

namespace OSVFS.Core.UnitTests.Sync;

/// <summary>
/// Verifies the provider-aware dispatch in <see cref="ChangeNotificationFactory"/>.
/// The S3 arm goes through <see cref="SqsChangeSourceFactory"/>; GCS / Azure
/// arms throw <see cref="NotSupportedException"/> with a "use polling" hint
/// until the matching backends land in Phase 2.
/// </summary>
public class ChangeNotificationFactoryTests
{
    [Fact]
    public void Create_returns_SqsChangeSource_for_S3_provider()
    {
        var source = ChangeNotificationFactory.Create(
            ObjectStoreProvider.S3,
            queueOrSubscription: "https://sqs.us-east-1.amazonaws.com/123456789012/osvfs-changes",
            bucketName: "my-bucket",
            keyPrefix: null,
            endpointUrl: null,
            region: "us-east-1",
            credentials: null,
            NullLoggerFactory.Instance);

        Assert.IsType<SqsChangeSource>(source);
        // Dispose the source so the SQS client it owns is released. Async dispose
        // returns a ValueTask; bridge to sync here since the test does not need
        // async semantics.
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_Gcs()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ChangeNotificationFactory.Create(
                ObjectStoreProvider.Gcs,
                queueOrSubscription: "projects/p/subscriptions/s",
                bucketName: "my-bucket",
                keyPrefix: null,
                endpointUrl: null,
                region: null,
                credentials: null,
                NullLoggerFactory.Instance));

        // The hint must point operators at the polling fallback; otherwise the
        // failure is just a dead end.
        Assert.Contains("polling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_returns_AzureStorageQueueChangeSource_for_AzureBlob_provider()
    {
        // Connection-string credentials let the factory resolve a bare queue
        // name without needing a fully-qualified queue URL — the same shape
        // an Azurite mount uses today.
        var source = ChangeNotificationFactory.Create(
            ObjectStoreProvider.AzureBlob,
            queueOrSubscription: "osvfs-changes",
            bucketName: "my-container",
            keyPrefix: null,
            endpointUrl: null,
            region: null,
            credentials: AzureCredentialSource.FromConnectionString(
                "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=Zm9v;EndpointSuffix=core.windows.net",
                "test"),
            NullLoggerFactory.Instance);

        Assert.IsType<AzureStorageQueueChangeSource>(source);
        await source.DisposeAsync();
    }

    [Fact]
    public void Create_throws_for_AzureBlob_when_credentials_are_missing()
    {
        // Storage Queue access requires credentials — there is no anonymous
        // path. The factory must surface that explicitly so the operator
        // hits a clear startup error rather than an opaque SDK 401.
        Assert.Throws<InvalidOperationException>(() =>
            ChangeNotificationFactory.Create(
                ObjectStoreProvider.AzureBlob,
                queueOrSubscription: "https://account.queue.core.windows.net/q",
                bucketName: "my-container",
                keyPrefix: null,
                endpointUrl: null,
                region: null,
                credentials: null,
                NullLoggerFactory.Instance));
    }
}
