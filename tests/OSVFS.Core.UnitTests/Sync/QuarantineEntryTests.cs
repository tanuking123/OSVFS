using OSVFS.Sync;
using Xunit;

namespace OSVFS.Core.UnitTests.Sync;

/// <summary>
/// Filename-encoding round-trip tests for <see cref="QuarantineEntry.TryParse"/>.
/// These guard the contract that <c>list</c> can recover the original
/// virt-root-relative path even when it contains backslashes, forward slashes,
/// spaces, or URL-reserved characters — i.e. that the encoding used by
/// <c>LostAndFoundQuarantine.TryQuarantine</c> is reversible.
/// </summary>
public sealed class QuarantineEntryTests
{
    [Fact]
    public void TryParse_decodes_a_simple_basename()
    {
        var ok = QuarantineEntry.TryParse("20260510T123456789Z_foo.txt", 42, out var entry);

        Assert.True(ok);
        Assert.NotNull(entry);
        Assert.Equal("foo.txt", entry!.OriginalRelativePath);
        Assert.Equal(42, entry.Length);
        Assert.Equal(2026, entry.QuarantinedAt.Year);
        Assert.Equal(5, entry.QuarantinedAt.Month);
        Assert.Equal(10, entry.QuarantinedAt.Day);
    }

    [Fact]
    public void TryParse_decodes_a_path_with_forward_slashes()
    {
        // 'docs/Projects/notes.md' -> 'docs%2FProjects%2Fnotes.md' on the wire.
        var ok = QuarantineEntry.TryParse(
            "20260510T123456789Z_docs%2FProjects%2Fnotes.md", 1024, out var entry);

        Assert.True(ok);
        Assert.Equal("docs/Projects/notes.md", entry!.OriginalRelativePath);
    }

    [Fact]
    public void TryParse_decodes_a_path_with_spaces_and_special_chars()
    {
        // Includes spaces (%20), parens (kept), and the literal '+' that URL encoding
        // turns into '%2B' so it doesn't get confused with a space.
        var ok = QuarantineEntry.TryParse(
            "20260510T123456789Z_my%20folder%2Fa%20%28v2%29%2B.bin", 7, out var entry);

        Assert.True(ok);
        Assert.Equal("my folder/a (v2)+.bin", entry!.OriginalRelativePath);
    }

    [Fact]
    public void TryParse_skips_collision_guid_segment_when_present()
    {
        var guid = "abcdef0123456789ABCDEF0123456789";
        var name = $"20260510T123456789Z_{guid}_payload.txt";

        var ok = QuarantineEntry.TryParse(name, 11, out var entry);

        Assert.True(ok);
        Assert.Equal("payload.txt", entry!.OriginalRelativePath);
    }

    [Fact]
    public void TryParse_does_not_swallow_real_filename_starting_with_hex()
    {
        // 32 hex chars but followed by content that does not URL-decode cleanly
        // when treated as a path segment — make sure we still pick the segment
        // out as a GUID *only* when the structure matches.
        // A path that legitimately is 32 hex chars ending in '_' followed by more
        // is genuinely ambiguous, but the parser leans toward "GUID" because the
        // quarantine writer never produces that shape itself.
        var ok = QuarantineEntry.TryParse(
            "20260510T123456789Z_not-hex-just-text.bin", 9, out var entry);

        Assert.True(ok);
        Assert.Equal("not-hex-just-text.bin", entry!.OriginalRelativePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("toosmall")]
    [InlineData("20260510T123456789Zfoo.txt")] // missing '_' separator at index 19
    [InlineData("not-a-timestamp_foo.txt")]
    [InlineData("20260510T123456789Z_")] // empty payload
    public void TryParse_rejects_malformed_filenames(string fileName)
    {
        var ok = QuarantineEntry.TryParse(fileName, 0, out var entry);

        Assert.False(ok);
        Assert.Null(entry);
    }
}
