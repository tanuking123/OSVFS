using System.Diagnostics;
using OSVFS.Net;
using Xunit;

namespace OSVFS.Core.UnitTests.Net;

/// <summary>
/// Time-sensitive tests over <see cref="TokenBucketRateLimiter"/>. The thresholds
/// are deliberately loose (≥ 95% of the theoretical floor) so the suite stays
/// stable on busy CI hosts.
/// </summary>
public class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_zero_or_negative_request_returns_immediately()
    {
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024);

        var sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(0, CancellationToken.None);
        await limiter.AcquireAsync(-5, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"Expected near-zero wait, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcquireAsync_first_acquire_is_throttled_from_t0()
    {
        // Bucket starts empty, so the first acquire must wait the full duration —
        // this is the property that makes end-to-end transfer time predictable on
        // small payloads.
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024);

        var sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(1024, CancellationToken.None);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 900, 2000);
    }

    [Fact]
    public async Task AcquireAsync_cancellation_propagates()
    {
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(1024, cts.Token));
    }

    [Fact]
    public async Task AcquireAsync_request_exceeding_burst_is_clamped_not_deadlocked()
    {
        // capacity is 1024, but caller asks for 8192. Limiter must still complete
        // (after consuming 1024 tokens — one second's worth) instead of blocking
        // forever waiting for a never-attainable balance.
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 1024, burstCapacityBytes: 1024);

        var sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(8192, CancellationToken.None);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 900, 2500);
    }

    [Fact]
    public void MaxBurstChunkSize_reflects_capacity()
    {
        using var limiter = new TokenBucketRateLimiter(bytesPerSecond: 5_000_000, burstCapacityBytes: 64 * 1024);
        Assert.Equal(64 * 1024, limiter.MaxBurstChunkSize);
    }

    [Fact]
    public void Constructor_rejects_non_positive_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(-1));
    }
}
