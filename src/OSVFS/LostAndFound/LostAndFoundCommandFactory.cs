using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Configuration;
using OSVFS.Credentials;
using OSVFS.ObjectStore;
using OSVFS.Sync;
using OSVFS.Sync.ProjFs;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OSVFS.LostAndFound;

/// <summary>
/// Builds the <c>osvfs lost-and-found</c> sub-command tree. The commands let the operator
/// inspect what the watcher quarantined into <c>.osvfs-lost+found</c>, diff a quarantined
/// copy against the current remote object, and copy a quarantined file back to a chosen
/// path. The implementation is intentionally light-weight: no ProjFS instance is started
/// and the running mount (if any) is not contacted.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LostAndFoundCommandFactory
{
    /// <summary>
    /// Constructs the <c>lost-and-found</c> command and its <c>list</c> / <c>diff</c> /
    /// <c>restore</c> children. <paramref name="credentialStore"/> is consumed only by
    /// <c>diff</c>, which has to talk to the remote backend to download the current copy.
    /// </summary>
    public static Command Build(IAwsCredentialStore credentialStore, MountCliOptions cliOptions)
    {
        var command = new Command(
            "lost-and-found",
            "Inspect and recover files quarantined under " +
            ObjectStoreChangeWatcher.LostAndFoundDirectoryName +
            " when the watcher overwrote a dirty local copy with the remote version.");
        command.Subcommands.Add(BuildListCommand(cliOptions));
        command.Subcommands.Add(BuildDiffCommand(credentialStore, cliOptions));
        command.Subcommands.Add(BuildRestoreCommand(cliOptions));
        return command;
    }

    /// <summary>
    /// Builds <c>lost-and-found list [--name &lt;mount&gt;]</c>. Prints one tab-separated
    /// row per quarantined file (quarantine filename, UTC timestamp, original relative
    /// path, size in bytes), newest first.
    /// </summary>
    private static Command BuildListCommand(MountCliOptions cliOptions)
    {
        var nameOption = NewMountNameOption();
        var command = new Command(
            "list",
            "List every file currently sitting in the mount's lost+found directory.")
        {
            nameOption,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            if (!TryResolveMount(parseResult, cliOptions, nameOption, out var mount)) return 2;
            var quarantine = OpenQuarantine(mount);
            var entries = quarantine.List();
            if (entries.Count == 0)
            {
                Console.WriteLine("(lost+found is empty)");
                return 0;
            }

            Console.WriteLine("FILENAME\tQUARANTINED-AT (UTC)\tORIGINAL-PATH\tSIZE");
            foreach (var entry in entries)
            {
                var ts = entry.QuarantinedAt.UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                Console.WriteLine(
                    $"{entry.FileName}\t{ts}\t{entry.OriginalRelativePath}\t{entry.Length}");
            }
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>lost-and-found diff &lt;quarantine-file&gt;</c>. Downloads the current
    /// remote object into a temporary file, then runs <c>git diff --no-index --color</c>
    /// for text payloads or prints a size-and-SHA-256 summary for binaries. Falls back to
    /// the binary summary when <c>git</c> is not on PATH.
    /// </summary>
    private static Command BuildDiffCommand(IAwsCredentialStore credentialStore, MountCliOptions cliOptions)
    {
        var nameOption = NewMountNameOption();
        var pathArg = new Argument<string>("quarantine-file")
        {
            Description = "Quarantine filename as printed by 'lost-and-found list' (the FILENAME column).",
        };
        var command = new Command(
            "diff",
            "Diff a quarantined file against the current remote object.")
        {
            pathArg,
            nameOption,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            if (!TryResolveMount(parseResult, cliOptions, nameOption, out var mount)) return 2;
            var quarantineFile = parseResult.GetValue(pathArg)!;
            var quarantine = OpenQuarantine(mount);
            var entry = quarantine.List()
                .FirstOrDefault(e => string.Equals(
                    e.FileName, quarantineFile, StringComparison.Ordinal));
            if (entry is null)
            {
                Console.Error.WriteLine(
                    $"No quarantined file named '{quarantineFile}'. Run 'osvfs lost-and-found list' " +
                    "to see the available filenames.");
                return 2;
            }

            var localPath = Path.Combine(
                mount.RootFolder!, ObjectStoreChangeWatcher.LostAndFoundDirectoryName, entry.FileName);
            var tempRemote = Path.Combine(
                Path.GetTempPath(), $"osvfs-remote-{Guid.NewGuid():N}.bin");

            try
            {
                using var backend = BuildBackend(mount, credentialStore);
                long? remoteLength;
                try
                {
                    remoteLength = DownloadRemote(backend, entry.OriginalRelativePath, tempRemote);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to download remote copy: {ex.Message}");
                    return 2;
                }

                if (remoteLength is null)
                {
                    Console.WriteLine(
                        $"Remote object '{entry.OriginalRelativePath}' does not currently exist; " +
                        "showing the quarantined copy only.");
                    Console.WriteLine($"Local quarantine: {localPath} ({entry.Length} bytes)");
                    return 0;
                }

                var localBinary = LooksBinary(localPath);
                var remoteBinary = LooksBinary(tempRemote);
                if (localBinary || remoteBinary)
                {
                    PrintBinarySummary(localPath, tempRemote, entry, remoteLength.Value);
                    return 0;
                }

                return RunGitDiff(localPath, tempRemote, entry, remoteLength.Value);
            }
            finally
            {
                TryDelete(tempRemote);
            }
        });
        return command;
    }

    /// <summary>
    /// Builds <c>lost-and-found restore &lt;quarantine-file&gt; [--target &lt;path&gt;]</c>.
    /// Copies a quarantined file out to <paramref name="--target"/>, defaulting to
    /// <c>./&lt;original basename&gt;</c> in the current working directory.
    /// </summary>
    private static Command BuildRestoreCommand(MountCliOptions cliOptions)
    {
        var nameOption = NewMountNameOption();
        var pathArg = new Argument<string>("quarantine-file")
        {
            Description = "Quarantine filename as printed by 'lost-and-found list'.",
        };
        var targetOption = new Option<string?>("--target")
        {
            Description =
                "Destination path (file or directory). Defaults to the original basename in " +
                "the current working directory. Existing files are not overwritten unless " +
                "--force is supplied.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite the destination if it already exists.",
        };
        var command = new Command(
            "restore",
            "Copy a quarantined file out of lost+found to a chosen path.")
        {
            pathArg,
            targetOption,
            forceOption,
            nameOption,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            if (!TryResolveMount(parseResult, cliOptions, nameOption, out var mount)) return 2;
            var quarantineFile = parseResult.GetValue(pathArg)!;
            var target = parseResult.GetValue(targetOption);
            var force = parseResult.GetValue(forceOption);

            var quarantine = OpenQuarantine(mount);
            var entry = quarantine.List()
                .FirstOrDefault(e => string.Equals(
                    e.FileName, quarantineFile, StringComparison.Ordinal));
            if (entry is null)
            {
                Console.Error.WriteLine(
                    $"No quarantined file named '{quarantineFile}'. Run 'osvfs lost-and-found list' " +
                    "to see the available filenames.");
                return 2;
            }

            var sourcePath = Path.Combine(
                mount.RootFolder!, ObjectStoreChangeWatcher.LostAndFoundDirectoryName, entry.FileName);

            var destination = ResolveRestoreDestination(target, entry.OriginalRelativePath);
            if (Directory.Exists(destination))
            {
                Console.Error.WriteLine(
                    $"Destination '{destination}' is an existing directory; pass --target with a " +
                    "file path inside it, or remove the directory.");
                return 2;
            }
            if (File.Exists(destination) && !force)
            {
                Console.Error.WriteLine(
                    $"Destination '{destination}' already exists. Re-run with --force to overwrite.");
                return 2;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(sourcePath, destination, overwrite: force);
            Console.WriteLine($"Restored '{entry.OriginalRelativePath}' to '{destination}'.");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Picks the destination path for <c>restore</c>. When <paramref name="target"/> is a
    /// directory the original basename is appended; when it is null the current working
    /// directory + original basename is used.
    /// </summary>
    private static string ResolveRestoreDestination(string? target, string originalRelativePath)
    {
        var basename = Path.GetFileName(originalRelativePath);
        if (string.IsNullOrEmpty(target))
        {
            return Path.Combine(Environment.CurrentDirectory, basename);
        }
        var fullTarget = Path.GetFullPath(target);
        if (Directory.Exists(fullTarget))
        {
            return Path.Combine(fullTarget, basename);
        }
        return fullTarget;
    }

    /// <summary>
    /// Heuristically classifies <paramref name="path"/> as binary by scanning the first
    /// 8 KiB for a NUL byte. Mirrors the approach <c>git</c> itself uses.
    /// </summary>
    private static bool LooksBinary(string path) => BinaryHeuristic.IsBinaryFile(path);

    /// <summary>
    /// Runs <c>git diff --no-index --color</c> between the quarantined copy and the
    /// downloaded remote, streaming git's output to stdout. Falls back to the binary
    /// summary when git is not available on PATH or fails to start.
    /// </summary>
    private static int RunGitDiff(string localPath, string remotePath, QuarantineEntry entry, long remoteLength)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--no-index");
        psi.ArgumentList.Add("--color");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(localPath);
        psi.ArgumentList.Add(remotePath);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not invoke 'git diff' ({ex.Message}); falling back to a size summary.");
            PrintBinarySummary(localPath, remotePath, entry, remoteLength);
            return 0;
        }

        if (process is null)
        {
            PrintBinarySummary(localPath, remotePath, entry, remoteLength);
            return 0;
        }

        process.WaitForExit();
        // git diff --no-index returns 0 when files are identical and 1 when they differ;
        // map both to a process exit code of 0 because the diff itself is the answer.
        // Anything else (>=2) means git aborted abnormally — surface it.
        return process.ExitCode >= 2 ? process.ExitCode : 0;
    }

    /// <summary>
    /// Prints a side-by-side size + SHA-256 summary for the binary (or non-text) case.
    /// Used when either file is binary or when <c>git</c> is unavailable.
    /// </summary>
    private static void PrintBinarySummary(
        string localPath, string remotePath, QuarantineEntry entry, long remoteLength)
    {
        var localHash = ComputeSha256(localPath);
        var remoteHash = ComputeSha256(remotePath);
        Console.WriteLine($"Original path: {entry.OriginalRelativePath}");
        Console.WriteLine(
            $"  local quarantine : {entry.Length,12} bytes  sha256={localHash}");
        Console.WriteLine(
            $"  current remote   : {remoteLength,12} bytes  sha256={remoteHash}");
        Console.WriteLine(
            string.Equals(localHash, remoteHash, StringComparison.Ordinal)
                ? "Files are identical."
                : "Files differ.");
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the file at <paramref name="path"/>.
    /// </summary>
    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Downloads the current remote copy of <paramref name="originalRelativePath"/> into
    /// <paramref name="localPath"/>. Returns the byte length, or null when the object no
    /// longer exists on the remote.
    /// </summary>
    private static long? DownloadRemote(
        IObjectStoreBackend backend, string originalRelativePath, string localPath)
    {
        var info = backend.HeadAsync(originalRelativePath, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (info is null || info.Value.IsDirectory) return null;

        var size = info.Value.Size;
        using var dest = new FileStream(
            localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        backend.ReadRangeAsync(originalRelativePath, 0, size, dest, CancellationToken.None)
            .GetAwaiter().GetResult();
        return size;
    }

    /// <summary>
    /// Builds a backend for the configured <paramref name="mount"/>, mirroring
    /// <see cref="MountOptionsBuilder"/> so the diff command sees the same credentials,
    /// region, endpoint, and prefix as the running mount would.
    /// </summary>
    private static IObjectStoreBackend BuildBackend(
        OsvfsMountConfig mount, IAwsCredentialStore credentialStore)
    {
        var options = MountOptionsBuilder.Build(
            mount, credentialStore, NullLogger.Instance, refreshNotifier: null);
        return ObjectStoreBackendFactory.Create(
            options.Provider,
            options.Bucket,
            options.EndpointUrl,
            options.KeyPrefix,
            options.Region,
            options.Credentials,
            options.BandwidthLimits,
            options.MultipartThresholdBytes,
            options.MultipartPartSizeBytes,
            options.RetryMaxAttempts,
            options.MaxConcurrentUploads,
            options.MaxConcurrentDownloads,
            options.MaxMultipartParts,
            options.RefreshNotifier);
    }

    /// <summary>
    /// Constructs a <see cref="LostAndFoundQuarantine"/> bound to the mount's
    /// <c>root-folder</c>. The logger is no-op because the CLI commands do their
    /// own user-facing reporting.
    /// </summary>
    private static LostAndFoundQuarantine OpenQuarantine(OsvfsMountConfig mount) =>
        new(mount.RootFolder!, NullLogger<LostAndFoundQuarantine>.Instance);

    /// <summary>
    /// Standard <c>--name</c> option used by every <c>lost-and-found</c> subcommand to
    /// pick a mount when more than one is configured.
    /// </summary>
    private static Option<string?> NewMountNameOption() => new("--name")
    {
        Description =
            "Selects a mount by 'name' from osvfs.toml. Required when the config defines " +
            "more than one [[mount]]; optional when there is exactly one.",
    };

    /// <summary>
    /// Loads the config file, picks the requested mount (or the only one), and validates
    /// that <c>root-folder</c> is set. Errors are written to stderr and the caller bails
    /// with a non-zero exit code.
    /// </summary>
    private static bool TryResolveMount(
        ParseResult parseResult,
        MountCliOptions cliOptions,
        Option<string?> nameOption,
        out OsvfsMountConfig mount)
    {
        mount = default!;
        var cliConfigPath = parseResult.GetValue(cliOptions.ConfigPath);
        OsvfsConfigFile? fileConfig;
        try
        {
            fileConfig = OsvfsConfigFileLoader.LoadFromDefaultLocations(cliConfigPath);
        }
        catch (OsvfsConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return false;
        }

        if (fileConfig is null || fileConfig.Mounts.Count == 0)
        {
            Console.Error.WriteLine(
                "No mount is configured. Create osvfs.toml or %APPDATA%/OSVFS/config.toml " +
                "with at least one [[mount]] entry.");
            return false;
        }

        var requested = parseResult.GetValue(nameOption);
        if (string.IsNullOrEmpty(requested))
        {
            if (fileConfig.Mounts.Count == 1)
            {
                mount = fileConfig.Mounts[0];
            }
            else
            {
                Console.Error.WriteLine(
                    $"Config defines {fileConfig.Mounts.Count} mounts; pass --name <one-of: " +
                    string.Join(", ", fileConfig.Mounts.Select(m => m.Name)) + ">.");
                return false;
            }
        }
        else
        {
            var match = fileConfig.Mounts.FirstOrDefault(m =>
                string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.Error.WriteLine(
                    $"No mount named '{requested}' in the config. Available: " +
                    string.Join(", ", fileConfig.Mounts.Select(m => m.Name)) + ".");
                return false;
            }
            mount = match;
        }

        if (string.IsNullOrEmpty(mount.RootFolder))
        {
            Console.Error.WriteLine(
                $"Mount '{mount.Name}' has no 'root-folder' configured.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Best-effort cleanup of a temp file; failures are silenced because they only leave
    /// behind a stray byte stream the OS will reap eventually.
    /// </summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
