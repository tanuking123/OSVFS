namespace OSVFS.ObjectStore;

/// <summary>
/// Sink that surfaces credential-refresh outcomes to the operator. Production
/// runs route the failure path through a Windows toast so a long-mounted
/// session can let the user know they need to re-login; success reports are
/// kept low-key (a single Information log line).
/// </summary>
internal interface ICredentialRefreshNotifier
{
    /// <summary>
    /// Reports a successful refresh of the credential identified by
    /// <paramref name="source"/>. <paramref name="newExpiresAt"/> carries the
    /// wall-clock expiration of the freshly-issued material when the upstream
    /// provider returned one.
    /// </summary>
    void NotifyRefreshed(string source, DateTimeOffset? newExpiresAt);

    /// <summary>
    /// Reports that a refresh attempt failed; <paramref name="error"/> carries
    /// the underlying exception for diagnostics. Implementations are expected
    /// to surface the failure via a high-visibility channel (toast, modal,
    /// pager) — by the time this fires the credential is unusable.
    /// </summary>
    void NotifyRefreshFailure(string source, Exception error);
}

/// <summary>
/// No-op default used by call sites that do not opt into refresh notifications
/// (tests, CLI commands that never trigger refresh).
/// </summary>
internal sealed class NullCredentialRefreshNotifier : ICredentialRefreshNotifier
{
    /// <summary>Process-wide singleton; no state to keep.</summary>
    public static NullCredentialRefreshNotifier Instance { get; } = new();

    /// <inheritdoc/>
    public void NotifyRefreshed(string source, DateTimeOffset? newExpiresAt)
    {
    }

    /// <inheritdoc/>
    public void NotifyRefreshFailure(string source, Exception error)
    {
    }
}
