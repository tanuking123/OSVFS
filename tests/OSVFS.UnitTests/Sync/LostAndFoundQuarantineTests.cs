using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.LostAndFound;
using OSVFS.Sync;
using OSVFS.Sync.ProjFs;
using Xunit;

namespace OSVFS.UnitTests.Sync;

/// <summary>
/// End-to-end tests over <see cref="LostAndFoundQuarantine"/> using a real temp
/// directory as the virtualization root. Together with the parsing-only unit
/// tests in OSVFS.Core, these cover the round-trip: quarantine -> list ->
/// re-read the bytes from disk.
/// </summary>
public sealed class LostAndFoundQuarantineTests
{
    [Fact]
    public void Quarantine_roundtrips_through_list_for_a_basename()
    {
        using var fs = new TempVirtRoot();
        const string relative = "report.txt";
        File.WriteAllText(Path.Combine(fs.Root, relative), "hello world");

        var quarantine = NewQuarantine(fs.Root);

        Assert.True(quarantine.TryQuarantine(relative));

        var entries = quarantine.List();

        var entry = Assert.Single(entries);
        Assert.Equal(relative, entry.OriginalRelativePath);
        Assert.Equal(11, entry.Length);

        var stored = Path.Combine(fs.Root, ObjectStoreChangeWatcher.LostAndFoundDirectoryName, entry.FileName);
        Assert.Equal("hello world", File.ReadAllText(stored));
    }

    [Fact]
    public void Quarantine_roundtrips_a_nested_path_with_special_characters()
    {
        using var fs = new TempVirtRoot();
        const string relative = "docs\\sub dir\\notes (v2)+.md";
        var fullPath = Path.Combine(fs.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "content");

        var quarantine = NewQuarantine(fs.Root);
        Assert.True(quarantine.TryQuarantine(relative));

        var entry = Assert.Single(quarantine.List());

        // List() always reports forward-slash-separated paths so the CLI output
        // is stable across platforms; the original could be supplied either way.
        Assert.Equal("docs/sub dir/notes (v2)+.md", entry.OriginalRelativePath);
    }

    [Fact]
    public void Two_quarantines_of_the_same_path_produce_two_distinct_entries()
    {
        using var fs = new TempVirtRoot();
        const string relative = "data.bin";
        var fullPath = Path.Combine(fs.Root, relative);

        var quarantine = NewQuarantine(fs.Root);
        File.WriteAllBytes(fullPath, new byte[] { 1, 2, 3 });
        Assert.True(quarantine.TryQuarantine(relative));

        // Force a different millisecond so the timestamp prefixes don't clash.
        Thread.Sleep(5);
        File.WriteAllBytes(fullPath, new byte[] { 4, 5 });
        Assert.True(quarantine.TryQuarantine(relative));

        var entries = quarantine.List();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(relative, e.OriginalRelativePath));
        // Newest first.
        Assert.True(entries[0].QuarantinedAt >= entries[1].QuarantinedAt);
        Assert.Equal(2, entries[0].Length);
        Assert.Equal(3, entries[1].Length);
    }

    [Fact]
    public void List_returns_empty_when_directory_does_not_exist()
    {
        using var fs = new TempVirtRoot();
        var quarantine = NewQuarantine(fs.Root);

        Assert.Empty(quarantine.List());
    }

    [Fact]
    public void List_skips_files_with_unparseable_names()
    {
        using var fs = new TempVirtRoot();
        var quarantineDir = Path.Combine(fs.Root, ObjectStoreChangeWatcher.LostAndFoundDirectoryName);
        Directory.CreateDirectory(quarantineDir);
        File.WriteAllText(Path.Combine(quarantineDir, "manual-readme.txt"), "x");
        File.WriteAllText(Path.Combine(quarantineDir, "20260510T123456789Z_real.txt"), "y");

        var entries = NewQuarantine(fs.Root).List();

        var entry = Assert.Single(entries);
        Assert.Equal("real.txt", entry.OriginalRelativePath);
    }

    [Fact]
    public void TryQuarantine_returns_false_when_source_is_missing()
    {
        using var fs = new TempVirtRoot();
        var quarantine = NewQuarantine(fs.Root);

        Assert.False(quarantine.TryQuarantine("does-not-exist.txt"));
        Assert.Empty(quarantine.List());
    }

    [Fact]
    public void BinaryHeuristic_reports_text_buffers_as_text()
    {
        var bytes = "the quick brown fox jumps over the lazy dog\nline two\n"u8;
        Assert.False(BinaryHeuristic.IsBinary(bytes));
    }

    [Fact]
    public void BinaryHeuristic_detects_nul_byte_as_binary()
    {
        var bytes = new byte[] { (byte)'h', (byte)'i', 0x00, (byte)'!' };
        Assert.True(BinaryHeuristic.IsBinary(bytes));
    }

    [Fact]
    public void BinaryHeuristic_only_inspects_first_8KiB()
    {
        // A NUL byte beyond the probe window does NOT flip the verdict.
        var buffer = new byte[BinaryHeuristic.ProbeBytes + 16];
        Array.Fill(buffer, (byte)'a');
        buffer[^1] = 0;

        Assert.False(BinaryHeuristic.IsBinary(buffer));
    }

    [Fact]
    public void BinaryHeuristic_classifies_real_files_via_path()
    {
        using var dir = new TempVirtRoot();
        var textPath = Path.Combine(dir.Root, "text.txt");
        var binaryPath = Path.Combine(dir.Root, "binary.bin");
        File.WriteAllText(textPath, "hello there\nstill text\n");
        File.WriteAllBytes(binaryPath, new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0 });

        Assert.False(BinaryHeuristic.IsBinaryFile(textPath));
        Assert.True(BinaryHeuristic.IsBinaryFile(binaryPath));
    }

    private static LostAndFoundQuarantine NewQuarantine(string syncRootPath) =>
        new(syncRootPath, NullLogger<LostAndFoundQuarantine>.Instance);

    /// <summary>
    /// Per-test temp directory acting as the virtualization root. Cleans up
    /// best-effort on dispose so failed tests don't leak GiB into %TEMP%.
    /// </summary>
    private sealed class TempVirtRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "osvfs-quarantine-" + Guid.NewGuid().ToString("N"));

        public TempVirtRoot() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }
}
