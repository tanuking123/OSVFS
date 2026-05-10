using System.Globalization;

namespace OSVFS.Sync;

/// <summary>
/// Single file inside the <c>.osvfs-lost+found</c> directory after the watcher
/// has overwritten a dirty local copy with the remote (authoritative) version.
/// Returned by <see cref="ILostAndFoundQuarantine.List"/> and consumed by the
/// <c>osvfs lost-and-found</c> CLI to drive list / diff / restore.
/// </summary>
internal sealed record QuarantineEntry
{
    /// <summary>
    /// Bare filename inside the lost+found directory (without any path prefix).
    /// Used as the user-facing identifier on the CLI.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// UTC timestamp at which the file was quarantined, parsed from the
    /// filename's <c>yyyyMMddTHHmmssfffZ</c> prefix.
    /// </summary>
    public required DateTimeOffset QuarantinedAt { get; init; }

    /// <summary>
    /// Original virtualization-root-relative path (with forward slashes) that
    /// was overwritten. Decoded from the URL-escaped portion of the filename.
    /// </summary>
    public required string OriginalRelativePath { get; init; }

    /// <summary>
    /// Size of the quarantined copy in bytes.
    /// </summary>
    public required long Length { get; init; }

    /// <summary>
    /// Width (19 chars) of the <c>yyyyMMddTHHmmssfffZ</c> timestamp prefix used
    /// in the filename. The 20th char is the literal <c>'_'</c> separator.
    /// </summary>
    private const int TimestampWidth = 19;

    /// <summary>
    /// Parses a lost+found filename produced by
    /// <see cref="ILostAndFoundQuarantine.TryQuarantine"/>. Returns <c>false</c>
    /// when the filename does not match the expected
    /// <c>yyyyMMddTHHmmssfffZ_&lt;url-encoded path&gt;</c> shape (legacy or
    /// foreign files inside the directory are skipped silently by the caller).
    /// </summary>
    public static bool TryParse(string fileName, long length, out QuarantineEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Length <= TimestampWidth + 1) return false;
        if (fileName[TimestampWidth] != '_') return false;

        var stamp = fileName.AsSpan(0, TimestampWidth);
        if (!DateTimeOffset.TryParseExact(
                stamp,
                "yyyyMMddTHHmmssfffZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var quarantinedAt))
        {
            return false;
        }

        var remainder = fileName.Substring(TimestampWidth + 1);

        // Sub-millisecond collisions inject a 32-char hex GUID followed by '_';
        // skip it before URL-decoding so the original path round-trips cleanly.
        if (remainder.Length > 33 && remainder[32] == '_' && IsHex32(remainder.AsSpan(0, 32)))
        {
            remainder = remainder.Substring(33);
        }

        string original;
        try
        {
            original = Uri.UnescapeDataString(remainder);
        }
        catch (UriFormatException)
        {
            return false;
        }

        entry = new QuarantineEntry
        {
            FileName = fileName,
            QuarantinedAt = quarantinedAt,
            OriginalRelativePath = original,
            Length = length,
        };
        return true;
    }

    /// <summary>
    /// Returns true when the span is exactly 32 lowercase or uppercase hex digits.
    /// </summary>
    private static bool IsHex32(ReadOnlySpan<char> span)
    {
        if (span.Length != 32) return false;
        foreach (var ch in span)
        {
            var hex = (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}
