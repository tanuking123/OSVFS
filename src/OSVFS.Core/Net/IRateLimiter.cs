namespace OSVFS.Net;

/// <summary>
/// Producer-side throttle that gates byte-level I/O. Implementations
/// hand out a fixed number of bytes per second; callers must request capacity
/// before transferring each chunk.
/// </summary>
internal interface IRateLimiter
{
    /// <summary>
    /// Sustained transfer ceiling in bytes per second.
    /// </summary>
    long BytesPerSecond { get; }

    /// <summary>
    /// Largest single <see cref="AcquireAsync"/> request implementations can serve in
    /// one shot. Callers should chunk larger transfers down to this size so a single
    /// request cannot starve concurrent waiters.
    /// </summary>
    int MaxBurstChunkSize { get; }

    /// <summary>
    /// Asynchronously waits until <paramref name="requestedBytes"/> of capacity is
    /// available, then deducts it from the bucket.
    /// </summary>
    Task AcquireAsync(int requestedBytes, CancellationToken ct);
}
