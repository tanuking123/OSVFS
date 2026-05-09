using System.Text;

namespace OSVFS.ObjectStore;

/// <summary>
/// Helpers for the user-defined metadata that providers expose as
/// <c>x-amz-meta-*</c> (S3) / custom blob metadata (Azure) / object metadata (GCS).
/// Centralizes the AWS-style 2 KiB total-size limit and the lowercase-key
/// normalization so backends and the ProjFS host agree on the shape.
/// </summary>
internal static class UserMetadata
{
    /// <summary>
    /// AWS caps the combined size of all <c>x-amz-meta-*</c> name+value pairs at
    /// 2 KiB per object. We pre-validate uploads against this limit so a typo
    /// fails fast instead of surfacing as an opaque PutObject 400.
    /// </summary>
    public const int MaxTotalBytes = 2 * 1024;

    /// <summary>
    /// Empty marker returned by <see cref="Normalize"/> when the input is null/empty,
    /// so callers can compare by reference without allocating.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Produces a lowercased-key, ordinal-comparing copy of <paramref name="metadata"/>,
    /// or <see cref="Empty"/> when the input is null/empty. AWS normalizes user
    /// metadata names to lowercase on the wire, so storing the same shape locally
    /// keeps round-trips bit-identical.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return Empty;

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrEmpty(key)) continue;
            copy[key.ToLowerInvariant()] = value ?? string.Empty;
        }
        return copy;
    }

    /// <summary>
    /// Throws <see cref="UserMetadataTooLargeException"/> when the combined UTF-8
    /// byte count of the (already-normalized) names and values exceeds
    /// <see cref="MaxTotalBytes"/>. Callers should run this against the same map
    /// they will hand to the backend so the validation matches what S3 sees.
    /// </summary>
    public static void EnsureWithinSizeLimit(IReadOnlyDictionary<string, string> metadata)
    {
        var total = 0;
        foreach (var (key, value) in metadata)
        {
            total += Encoding.UTF8.GetByteCount(key);
            total += Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (total > MaxTotalBytes)
            {
                throw new UserMetadataTooLargeException(total);
            }
        }
    }
}

/// <summary>
/// Thrown when a caller asks the backend to attach more user metadata than the
/// provider's per-object limit (2 KiB on S3) allows.
/// </summary>
internal sealed class UserMetadataTooLargeException(int actualBytes)
    : InvalidOperationException(
        $"User metadata is {actualBytes} bytes, exceeding the {UserMetadata.MaxTotalBytes}-byte per-object limit.")
{
    /// <summary>
    /// Combined UTF-8 byte count of the supplied name/value pairs at the moment
    /// the limit was breached.
    /// </summary>
    public int ActualBytes { get; } = actualBytes;
}
