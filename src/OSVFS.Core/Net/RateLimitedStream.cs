namespace OSVFS.Net;

/// <summary>
/// <see cref="Stream"/> wrapper that throttles reads through an <see cref="IRateLimiter"/>.
/// Used to wrap the input stream of an upload (so the SDK's pulls are paced) and the
/// response stream of a download (so the local copy loop is paced). All non-read
/// operations passthrough to the inner stream so an SDK that needs <see cref="Length"/>
/// or seeking still gets the right answer.
/// </summary>
internal sealed class RateLimitedStream : Stream
{
    private readonly Stream inner;
    private readonly IRateLimiter limiter;
    private readonly bool leaveOpen;

    /// <summary>
    /// Creates a rate-limited view over <paramref name="inner"/>. When <paramref name="leaveOpen"/>
    /// is true, disposing this wrapper does not dispose the inner stream — required when the
    /// caller (e.g. ProjFsProvider) owns the underlying stream's lifetime.
    /// </summary>
    public RateLimitedStream(Stream inner, IRateLimiter limiter, bool leaveOpen = true)
    {
        this.inner = inner;
        this.limiter = limiter;
        this.leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value) => inner.SetLength(value);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return inner.Read(buffer, offset, count);
        var chunk = Math.Min(count, limiter.MaxBurstChunkSize);
        // Synchronous wait on a bounded chunk: the SDK uses sync Read paths in places
        // (TransferUtility multipart workers, Newtonsoft serializers). Blocking here is
        // the only correct behavior — we can't return less than requested without breaking
        // protocol assumptions.
        limiter.AcquireAsync(chunk, CancellationToken.None).GetAwaiter().GetResult();
        return inner.Read(buffer, offset, chunk);
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        var chunk = Math.Min(count, limiter.MaxBurstChunkSize);
        await limiter.AcquireAsync(chunk, cancellationToken).ConfigureAwait(false);
        return await inner.ReadAsync(buffer.AsMemory(offset, chunk), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var chunkLen = Math.Min(buffer.Length, limiter.MaxBurstChunkSize);
        await limiter.AcquireAsync(chunkLen, cancellationToken).ConfigureAwait(false);
        return await inner.ReadAsync(buffer[..chunkLen], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    /// <summary>
    /// Disposes the inner stream unless <c>leaveOpen</c> was supplied at construction.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
