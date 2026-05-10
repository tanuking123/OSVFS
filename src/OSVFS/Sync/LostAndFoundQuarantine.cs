using Microsoft.Extensions.Logging;
using OSVFS.Sync;
using System.Globalization;

namespace OSVFS.Sync.ProjFs;

/// <summary>
/// File-system implementation of <see cref="ILostAndFoundQuarantine"/>. Copies the dirty local
/// copy into a sibling lost+found directory beneath the virtualization root before the watcher
/// replaces it with the remote (authoritative) version.
/// </summary>
internal sealed class LostAndFoundQuarantine(
    string syncRootPath,
    ILogger<LostAndFoundQuarantine> logger)
    : ILostAndFoundQuarantine
{
    private readonly string lostAndFoundDir =
        Path.Combine(syncRootPath, ObjectStoreChangeWatcher.LostAndFoundDirectoryName);
    private readonly string syncRootPath = syncRootPath;

    /// <summary>
    /// Returns the absolute path to the lost+found directory backing this quarantine.
    /// Used by the CLI <c>lost-and-found</c> subcommand to read entry contents.
    /// </summary>
    public string Directory => lostAndFoundDir;

    /// <inheritdoc/>
    public bool TryQuarantine(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;

        var fullPath = Path.Combine(syncRootPath, relativePath);
        if (!File.Exists(fullPath))
        {
            // Nothing to save (e.g. the user deleted the file before we got here, or it was
            // a metadata-only conflict on a never-hydrated placeholder).
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(lostAndFoundDir);

            // Per the spec, the lost+found name is prefixed with an identifier so successive
            // versions of the same path don't collide. We use a UTC timestamp; if that still
            // collides (unlikely sub-millisecond), fall back to a random suffix.
            var stamp = DateTimeOffset.UtcNow.ToString(
                "yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);

            // URL-encoding makes the filename reversible (List() can recover the original
            // virt-root-relative path) while still being filename-safe on NTFS.
            var safeName = Uri.EscapeDataString(relativePath.Replace('\\', '/'));
            var destination = Path.Combine(lostAndFoundDir, $"{stamp}_{safeName}");
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    lostAndFoundDir, $"{stamp}_{Guid.NewGuid():N}_{safeName}");
            }

            // Read+write rather than File.Copy: ProjFS placeholders that have been hydrated
            // expose their data through normal NTFS, but reading via FileStream avoids any
            // surprises with copy-tier short-circuits.
            using (var src = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            using (var dst = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            {
                src.CopyTo(dst);
            }

            logger.LogInformation(
                "Quarantined dirty local copy of {Path} to {Destination}.", relativePath, destination);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to quarantine {Path}.", relativePath);
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<QuarantineEntry> List()
    {
        if (!System.IO.Directory.Exists(lostAndFoundDir))
        {
            return Array.Empty<QuarantineEntry>();
        }

        var entries = new List<QuarantineEntry>();
        foreach (var path in System.IO.Directory.EnumerateFiles(
                     lostAndFoundDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (QuarantineEntry.TryParse(fileName, info.Length, out var entry) && entry is not null)
            {
                entries.Add(entry);
            }
        }
        entries.Sort(static (a, b) => b.QuarantinedAt.CompareTo(a.QuarantinedAt));
        return entries;
    }
}
