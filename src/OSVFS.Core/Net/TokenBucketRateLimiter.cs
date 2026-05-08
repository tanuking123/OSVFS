using System.Diagnostics;

namespace OSVFS.Net;

/// <summary>
/// Token-bucket <see cref="IRateLimiter"/> backed by a <see cref="SemaphoreSlim"/>
/// for mutual exclusion and a <see cref="Stopwatch"/> for monotonic timing.
/// The bucket starts empty so the very first acquire is throttled — this gives
/// callers a predictable end-to-end transfer time on small payloads instead of
/// letting a full burst hide the rate limit.
/// </summary>
internal sealed class TokenBucketRateLimiter : IRateLimiter, IDisposable
{
    private readonly long bytesPerSecond;
    private readonly long burstCapacityBytes;
    private readonly double tokensPerMillisecond;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private double tokens;
    private long lastRefillElapsedMs;

    /// <summary>
    /// Creates a limiter that sustains <paramref name="bytesPerSecond"/> on average
    /// and tolerates short bursts up to <paramref name="burstCapacityBytes"/>. When
    /// the burst argument is omitted, defaults to one second of capacity.
    /// </summary>
    public TokenBucketRateLimiter(long bytesPerSecond, long? burstCapacityBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        this.bytesPerSecond = bytesPerSecond;
        this.burstCapacityBytes = burstCapacityBytes ?? bytesPerSecond;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.burstCapacityBytes);
        tokensPerMillisecond = bytesPerSecond / 1000.0;
        // Start empty so the first acquire pays the full latency cost; otherwise the
        // bucket would discharge an entire burst worth of bytes for free at t=0.
        tokens = 0;
        lastRefillElapsedMs = 0;
    }

    /// <inheritdoc/>
    public long BytesPerSecond => bytesPerSecond;

    /// <inheritdoc/>
    public int MaxBurstChunkSize => (int)Math.Min(burstCapacityBytes, int.MaxValue);

    /// <inheritdoc/>
    public async Task AcquireAsync(int requestedBytes, CancellationToken ct)
    {
        if (requestedBytes <= 0) return;

        var clamped = Math.Min(requestedBytes, MaxBurstChunkSize);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long delayMs;
            await mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Refill();
                if (tokens >= clamped)
                {
                    tokens -= clamped;
                    return;
                }
                var deficit = clamped - tokens;
                delayMs = (long)Math.Ceiling(deficit / tokensPerMillisecond);
                if (delayMs <= 0) delayMs = 1;
            }
            finally
            {
                mutex.Release();
            }

            // Delay outside the critical section so concurrent acquires can also wait.
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the internal semaphore.
    /// </summary>
    public void Dispose() => mutex.Dispose();

    /// <summary>
    /// Adds tokens accrued since the last refill, capped at <see cref="burstCapacityBytes"/>.
    /// Caller must hold <see cref="mutex"/>.
    /// </summary>
    private void Refill()
    {
        var now = stopwatch.ElapsedMilliseconds;
        var elapsed = now - lastRefillElapsedMs;
        if (elapsed <= 0) return;
        var newTokens = tokens + elapsed * tokensPerMillisecond;
        tokens = Math.Min(burstCapacityBytes, newTokens);
        lastRefillElapsedMs = now;
    }
}
