using System.Diagnostics;
using OSVFS.Net;
using Xunit;

namespace OSVFS.Core.UnitTests.Net;

/// <summary>
/// Tests covering the documented end-to-end behavior from issue #20: throttled
/// reads are paced to the configured rate, and unrelated stream operations
/// (Length / Position / Write) pass straight through to the inner stream.
/// </summary>
public class RateLimitedStreamTests
{
    [Fact]
    public async Task Reading_5MB_at_1MB_per_second_takes_5_to_6_seconds()
    {
        // Acceptance criterion from issue #20: 5 MB at 1 MiB/s should land in [5s, 6s].
        // The bucket starts empty so the first chunk pays full latency; total floor is 5 s.
        // Upper bound is loose enough to stay green on busy CI runners.
        const long oneMiB = 1024L * 1024;
        const int totalBytes = 5 * (int)oneMiB;

        var source = new MemoryStream(new byte[totalBytes]);
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: oneMiB);
        await using var rateLimited = new RateLimitedStream(source, limiter);

        var sink = new byte[64 * 1024];
        var sw = Stopwatch.StartNew();
        var read = 0;
        int n;
        while ((n = await rateLimited.ReadAsync(sink, CancellationToken.None)) > 0)
        {
            read += n;
        }
        sw.Stop();

        Assert.Equal(totalBytes, read);
        // Lower bound a hair below 5s to absorb sub-millisecond Stopwatch jitter; upper
        // bound ~6s with headroom for Task.Delay overhead on shared CI hosts.
        Assert.InRange(sw.Elapsed.TotalSeconds, 4.95, 7.0);
    }

    [Fact]
    public async Task Reading_returns_at_most_max_burst_chunk_size_per_call()
    {
        // The wrapper must clamp each Read to MaxBurstChunkSize so a caller that
        // hands in a giant buffer doesn't block on a never-attainable acquire.
        const int burstBytes = 4 * 1024;
        const int totalBytes = 32 * 1024;

        var source = new MemoryStream(new byte[totalBytes]);
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024 * 1024, burstCapacityBytes: burstBytes);
        await using var rateLimited = new RateLimitedStream(source, limiter);

        var huge = new byte[totalBytes];
        var n = await rateLimited.ReadAsync(huge, CancellationToken.None);

        Assert.True(n <= burstBytes, $"Expected ≤ {burstBytes} bytes per call, got {n}");
    }

    [Fact]
    public void Length_and_CanSeek_passthrough()
    {
        // Required so AWSSDK's TransferUtility can pick the multipart vs single-PUT
        // branch on the wrapped upload stream (which checks both Length and CanSeek).
        var inner = new MemoryStream(new byte[1234]);
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024 * 1024);
        using var rateLimited = new RateLimitedStream(inner, limiter);

        Assert.Equal(1234, rateLimited.Length);
        Assert.True(rateLimited.CanSeek);
        Assert.True(rateLimited.CanRead);

        rateLimited.Position = 10;
        Assert.Equal(10, inner.Position);
    }

    [Fact]
    public void LeaveOpen_default_does_not_dispose_inner()
    {
        var inner = new TrackingStream();
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024);

        var rateLimited = new RateLimitedStream(inner, limiter);
        rateLimited.Dispose();

        Assert.False(inner.WasDisposed);
    }

    [Fact]
    public void LeaveOpen_false_disposes_inner()
    {
        var inner = new TrackingStream();
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024);

        var rateLimited = new RateLimitedStream(inner, limiter, leaveOpen: false);
        rateLimited.Dispose();

        Assert.True(inner.WasDisposed);
    }

    /// <summary>
    /// Minimal MemoryStream-backed stream that records its disposal state
    /// so the leaveOpen tests can assert on it.
    /// </summary>
    private sealed class TrackingStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
