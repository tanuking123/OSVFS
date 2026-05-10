namespace OSVFS.LostAndFound;

/// <summary>
/// Cheap "is this binary?" heuristic shared between the <c>lost-and-found diff</c>
/// command and its tests. Matches the approach git itself uses: scan the first 8 KiB
/// for a NUL byte and treat any hit as binary.
/// </summary>
internal static class BinaryHeuristic
{
    /// <summary>
    /// Probe size in bytes. 8 KiB is large enough to catch null bytes near the start
    /// of common binary formats (PE, ELF, ZIP, PNG, JPEG) without slurping huge files.
    /// </summary>
    internal const int ProbeBytes = 8192;

    /// <summary>
    /// Returns true when <paramref name="bytes"/> contains at least one NUL byte
    /// inside the first <see cref="ProbeBytes"/> positions.
    /// </summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length > ProbeBytes ? bytes[..ProbeBytes] : bytes;
        for (var i = 0; i < window.Length; i++)
        {
            if (window[i] == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the file at <paramref name="path"/> looks binary. Read
    /// failures are reported as binary because we cannot safely diff the file
    /// either way; the caller's binary fall-back is the conservative choice.
    /// </summary>
    public static bool IsBinaryFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[ProbeBytes];
            var read = stream.Read(buffer);
            return IsBinary(buffer[..read]);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
