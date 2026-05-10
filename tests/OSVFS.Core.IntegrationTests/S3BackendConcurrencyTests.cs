using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Verifies the per-direction concurrency gates wired through
/// <see cref="S3Backend.MaxConcurrentUploads"/> /
/// <see cref="S3Backend.MaxConcurrentDownloads"/>. Uses LocalStack so the
/// gate is exercised against a real network round-trip rather than a mocked
/// client — the in-flight counter is sampled from the live semaphore while a
/// burst of operations is in flight.
/// </summary>
[Collection(LocalStackCollection.Name)]
public sealed class S3BackendConcurrencyTests : IAsyncLifetime
{
    private readonly LocalStackFixture localStack;
    private readonly string bucket = $"osvfs-{Guid.NewGuid():N}";
    private AmazonS3Client adminClient = null!;

    public S3BackendConcurrencyTests(LocalStackFixture localStack)
    {
        this.localStack = localStack;
    }

    public async Task InitializeAsync()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = localStack.ServiceUrl,
            ForcePathStyle = true,
        };
        adminClient = new AmazonS3Client(config);
        await adminClient.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    }

    public async Task DisposeAsync()
    {
        try
        {
            await EmptyBucketAsync();
            await adminClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception)
        {
            // Best-effort cleanup; the next test creates its own bucket.
        }
        adminClient.Dispose();
    }

    [Fact]
    public async Task UploadAsync_never_exceeds_MaxConcurrentUploads()
    {
        const int max = 3;
        const int totalUploads = 30;

        using var backend = new S3Backend(
            bucket,
            localStack.ServiceUrl,
            maxConcurrentUploads: max);

        // Sample the in-flight upload count from a background ticker while a
        // burst of uploads is active. The gate guarantees the live count is
        // never above `max`, regardless of how many tasks are queued.
        using var stopSampling = new CancellationTokenSource();
        var maxObserved = 0;
        var samplerTask = Task.Run(async () =>
        {
            while (!stopSampling.Token.IsCancellationRequested)
            {
                var inFlight = backend.MaxConcurrentUploads - backend.CurrentUploadPermits;
                if (inFlight > maxObserved) maxObserved = inFlight;
                try
                {
                    await Task.Delay(1, stopSampling.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var payload = new byte[16 * 1024];
        Random.Shared.NextBytes(payload);

        var uploads = Enumerable.Range(0, totalUploads)
            .Select(i => Task.Run(async () =>
            {
                using var stream = new MemoryStream(payload, writable: false);
                await backend.UploadAsync(
                    $"concurrency/u-{i}.bin", stream, ifMatchETag: null, CancellationToken.None);
            }))
            .ToArray();

        await Task.WhenAll(uploads);
        stopSampling.Cancel();
        await samplerTask;

        Assert.True(maxObserved > 0, "Sampler must have observed at least one in-flight upload.");
        Assert.True(
            maxObserved <= max,
            $"In-flight uploads peaked at {maxObserved}; ceiling was {max}.");
        // Once everything completes the gate must be fully released.
        Assert.Equal(max, backend.CurrentUploadPermits);
    }

    [Fact]
    public async Task ReadRangeAsync_never_exceeds_MaxConcurrentDownloads()
    {
        const int max = 2;
        const int totalReads = 20;

        using var backend = new S3Backend(
            bucket,
            localStack.ServiceUrl,
            maxConcurrentDownloads: max);

        // Pre-seed an object so the downloads have something to fetch. Use the
        // backend's own UploadAsync so the body is exactly what ReadRangeAsync
        // will return.
        var payload = new byte[8 * 1024];
        Random.Shared.NextBytes(payload);
        using (var seedStream = new MemoryStream(payload, writable: false))
        {
            await backend.UploadAsync(
                "concurrency/source.bin", seedStream, ifMatchETag: null, CancellationToken.None);
        }

        using var stopSampling = new CancellationTokenSource();
        var maxObserved = 0;
        var samplerTask = Task.Run(async () =>
        {
            while (!stopSampling.Token.IsCancellationRequested)
            {
                var inFlight = backend.MaxConcurrentDownloads - backend.CurrentDownloadPermits;
                if (inFlight > maxObserved) maxObserved = inFlight;
                try
                {
                    await Task.Delay(1, stopSampling.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var reads = Enumerable.Range(0, totalReads)
            .Select(_ => Task.Run(async () =>
            {
                using var sink = new MemoryStream();
                await backend.ReadRangeAsync(
                    "concurrency/source.bin", 0, payload.Length, sink, CancellationToken.None);
            }))
            .ToArray();

        await Task.WhenAll(reads);
        stopSampling.Cancel();
        await samplerTask;

        Assert.True(maxObserved > 0, "Sampler must have observed at least one in-flight download.");
        Assert.True(
            maxObserved <= max,
            $"In-flight downloads peaked at {maxObserved}; ceiling was {max}.");
        Assert.Equal(max, backend.CurrentDownloadPermits);
    }

    private async Task EmptyBucketAsync()
    {
        var listing = await adminClient.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
        });

        var objects = listing.S3Objects;
        if (objects is null || objects.Count == 0) return;

        await adminClient.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = objects
                .Where(o => !string.IsNullOrEmpty(o.Key))
                .Select(o => new KeyVersion { Key = o.Key })
                .ToList(),
            Quiet = true,
        });
    }
}
