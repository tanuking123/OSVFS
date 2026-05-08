using System.Globalization;

namespace OSVFS.Net;

/// <summary>
/// Parses rclone-style bandwidth values into bytes-per-second. Supported forms:
/// <list type="bullet">
///   <item><description><c>0</c> or empty — disabled (null)</description></item>
///   <item><description><c>500</c> — bytes/s (no suffix)</description></item>
///   <item><description><c>5K</c> — KiB/s (1024 bytes/s)</description></item>
///   <item><description><c>5M</c> — MiB/s</description></item>
///   <item><description><c>5G</c> — GiB/s</description></item>
/// </list>
/// Suffixes are case-insensitive; trailing <c>B</c> or <c>iB</c> (e.g. <c>5MiB</c>,
/// <c>5MB</c>) is tolerated for readability.
/// </summary>
internal static class BandwidthSize
{
    /// <summary>
    /// Parses <paramref name="raw"/> into a bytes-per-second value, or returns null
    /// when the input is empty or zero. Throws <see cref="FormatException"/> on
    /// malformed input.
    /// </summary>
    public static long? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        var (numberText, multiplier) = SplitSuffix(trimmed);

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new FormatException(
                $"Invalid bandwidth value '{raw}'. Expected a non-negative number with optional K/M/G suffix.");
        }

        var bytesPerSecond = (long)Math.Round(value * multiplier);
        return bytesPerSecond <= 0 ? null : bytesPerSecond;
    }

    /// <summary>
    /// Splits the trailing unit from the numeric portion. Recognizes K/M/G optionally
    /// followed by <c>i</c>/<c>iB</c>/<c>B</c>; a lone <c>B</c> is also accepted.
    /// </summary>
    private static (string Number, long Multiplier) SplitSuffix(string trimmed)
    {
        var work = trimmed;
        // Strip a trailing optional "iB", "B" so "5MB" / "5MiB" / "5M" all parse the same.
        if (work.EndsWith("iB", StringComparison.OrdinalIgnoreCase))
        {
            work = work[..^2];
        }
        else if (work.EndsWith('B') || work.EndsWith('b'))
        {
            work = work[..^1];
        }

        if (work.Length == 0) return (work, 1L);

        var last = char.ToUpperInvariant(work[^1]);
        var (multiplier, hasSuffix) = last switch
        {
            'K' => (1024L, true),
            'M' => (1024L * 1024, true),
            'G' => (1024L * 1024 * 1024, true),
            _ => (1L, false),
        };

        return hasSuffix ? (work[..^1], multiplier) : (work, multiplier);
    }
}
