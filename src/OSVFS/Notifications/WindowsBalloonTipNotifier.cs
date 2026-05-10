using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OSVFS.Notifications;

/// <summary>
/// AOT-friendly <see cref="ICredentialRefreshNotifier"/> backed by the Win32
/// <c>Shell_NotifyIconW</c> API. Uses a transient tray icon plus a balloon-tip
/// payload so the notification survives even when the OSVFS process has no
/// active window — important because long-running mounts run as a background
/// CLI, not a desktop app. Falls back to writing through
/// <see cref="ILogger"/> when the underlying call fails (headless CI, session
/// 0 service, missing shell) so the notification is never silently dropped.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsBalloonTipNotifier : ICredentialRefreshNotifier, IDisposable
{
    /// <summary>NIM_ADD: register a new icon.</summary>
    private const uint NimAdd = 0x00000000;

    /// <summary>NIM_MODIFY: update an existing icon (used to push the balloon).</summary>
    private const uint NimModify = 0x00000001;

    /// <summary>NIM_DELETE: tear the icon down.</summary>
    private const uint NimDelete = 0x00000002;

    /// <summary>NIF_MESSAGE | NIF_ICON | NIF_TIP: standard tray-icon fields.</summary>
    private const uint NifBase = 0x00000001 | 0x00000002 | 0x00000004;

    /// <summary>NIF_INFO: balloon-tip fields are populated.</summary>
    private const uint NifInfo = 0x00000010;

    /// <summary>NIF_GUID: identifies the icon by GUID rather than by uID.</summary>
    private const uint NifGuid = 0x00000020;

    /// <summary>NIIF_WARNING: yellow-triangle glyph in the balloon.</summary>
    private const uint NiifWarning = 0x00000002;

    /// <summary>IDI_INFORMATION resource identifier for LoadIconW.</summary>
    private static readonly IntPtr IdiInformation = 32516;

    /// <summary>Balloon-tip title text. 64 chars max per Win32.</summary>
    private const string AppTitle = "OSVFS";

    /// <summary>
    /// Stable GUID identifying this notifier's tray icon across NIM_ADD /
    /// NIM_MODIFY / NIM_DELETE calls. Process-private; survives the lifetime
    /// of one OSVFS instance.
    /// </summary>
    private static readonly Guid IconGuid = new("8B1F6E5C-3D4A-4E1D-9F0E-7C2B5A0A0A0A");

    private readonly ILogger<WindowsBalloonTipNotifier> logger;
    private readonly object addLock = new();

    private bool iconAdded;
    private bool disposed;

    /// <summary>
    /// Constructs the notifier. The tray icon is registered lazily on the
    /// first refresh failure so processes that never trip a refresh failure
    /// incur no Win32 cost.
    /// </summary>
    public WindowsBalloonTipNotifier(ILogger<WindowsBalloonTipNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <inheritdoc/>
    public void NotifyRefreshed(string source, DateTimeOffset? newExpiresAt)
    {
        // Success path is intentionally low-key per the Issue spec: a single
        // Information line, no toast (don't pull the user back into OSVFS just
        // because the SDK rolled the credential over silently).
        if (newExpiresAt is { } when_)
        {
            logger.LogInformation(
                "Refreshed AWS credentials from {Source}; new expiry {ExpiresAt:u}.",
                source, when_);
        }
        else
        {
            logger.LogInformation("Refreshed AWS credentials from {Source}.", source);
        }
    }

    /// <inheritdoc/>
    public void NotifyRefreshFailure(string source, Exception error)
    {
        logger.LogError(
            error,
            "Failed to refresh AWS credentials from {Source}; toast notification will be raised.",
            source);

        var message =
            $"Could not refresh AWS credentials ({source}). " +
            "Re-run 'osvfs credentials sso ...' (or 'aws login') to re-authenticate.";

        if (!TryShowBalloon("OSVFS: AWS credentials expired", message, NiifWarning))
        {
            logger.LogWarning("Toast notification failed; user may not see {Message}", message);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (iconAdded)
        {
            TryRemoveIcon();
        }
    }

    /// <summary>
    /// Registers the tray icon (idempotent under <see cref="addLock"/>) and
    /// pushes a balloon. Returns false on any Win32 failure so the caller can
    /// fall back to log-only.
    /// </summary>
    private bool TryShowBalloon(string title, string message, uint icon)
    {
        try
        {
            lock (addLock)
            {
                if (!iconAdded)
                {
                    if (!AddIcon())
                    {
                        logger.LogDebug(
                            "Shell_NotifyIcon NIM_ADD returned false (err={Err}).",
                            Marshal.GetLastPInvokeError());
                        return false;
                    }
                    iconAdded = true;
                }
            }
            if (!ModifyIconWithBalloon(title, message, icon))
            {
                logger.LogDebug(
                    "Shell_NotifyIcon NIM_MODIFY returned false (err={Err}).",
                    Marshal.GetLastPInvokeError());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Shell_NotifyIcon raised an unexpected exception.");
            return false;
        }
    }

    /// <summary>First-time NIM_ADD with NIF_GUID + NIF_TIP.</summary>
    private static bool AddIcon()
    {
        var data = BuildBaseData();
        return Shell_NotifyIcon(NimAdd, ref data);
    }

    /// <summary>NIM_MODIFY with NIF_INFO populates the balloon-tip fields.</summary>
    private static bool ModifyIconWithBalloon(string title, string message, uint icon)
    {
        var data = BuildBaseData();
        data.uFlags |= NifInfo;
        data.dwInfoFlags = icon;
        WriteString(message, ref data.szInfo);
        WriteString(title, ref data.szInfoTitle);
        return Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Best-effort NIM_DELETE; OS will reap the icon at process exit anyway.</summary>
    private static bool TryRemoveIcon()
    {
        var data = BuildBaseData();
        return Shell_NotifyIcon(NimDelete, ref data);
    }

    /// <summary>
    /// Builds the shared NOTIFYICONDATAW header (icon, GUID identity, tip).
    /// </summary>
    private static NotifyIconData BuildBaseData()
    {
        var data = default(NotifyIconData);
        data.cbSize = (uint)Unsafe.SizeOf<NotifyIconData>();
        data.hWnd = IntPtr.Zero;
        data.uID = 0;
        data.uFlags = NifBase | NifGuid;
        data.uCallbackMessage = 0;
        data.hIcon = LoadIcon(IntPtr.Zero, IdiInformation);
        data.guidItem = IconGuid;
        WriteString(AppTitle, ref data.szTip);
        return data;
    }

    /// <summary>
    /// Copies <paramref name="value"/> into the inline char buffer starting at
    /// <paramref name="buffer"/>, truncating to one less than the buffer size
    /// to leave room for the trailing NUL the Win32 API expects on every
    /// fixed-size WCHAR field.
    /// </summary>
    private static void WriteString<T>(string value, ref T buffer) where T : struct
    {
        var span = MemoryMarshal.CreateSpan(ref Unsafe.As<T, char>(ref buffer), Unsafe.SizeOf<T>() / sizeof(char));
        span.Clear();
        var copyLen = Math.Min(value.Length, span.Length - 1);
        if (copyLen > 0) value.AsSpan(0, copyLen).CopyTo(span);
    }

    /// <summary>
    /// NOTIFYICONDATAW interop layout — one-to-one with shellapi.h, using
    /// AOT-safe <see cref="InlineArrayAttribute"/> char buffers in place of
    /// the legacy <c>ByValTStr</c> marshalling that the LibraryImport source
    /// generator does not support.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public WChar128 szTip;
        public uint dwState;
        public uint dwStateMask;
        public WChar256 szInfo;
        public uint uTimeoutOrVersion;
        public WChar64 szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>Inline 64-WCHAR buffer for szInfoTitle.</summary>
    [InlineArray(64)]
    private struct WChar64
    {
        private char element0;
    }

    /// <summary>Inline 128-WCHAR buffer for szTip.</summary>
    [InlineArray(128)]
    private struct WChar128
    {
        private char element0;
    }

    /// <summary>Inline 256-WCHAR buffer for szInfo.</summary>
    [InlineArray(256)]
    private struct WChar256
    {
        private char element0;
    }

    /// <summary>
    /// Shell_NotifyIconW: registers, modifies, or removes a notification-area
    /// icon. Only the W variant is exposed so all char fields are UTF-16.
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    /// <summary>
    /// LoadIconW: loads a stock OS icon (IDI_INFORMATION etc.). Spares us
    /// from embedding our own .ico resource.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);
}
