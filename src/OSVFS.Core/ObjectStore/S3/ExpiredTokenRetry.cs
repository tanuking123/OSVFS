using Amazon.Runtime;
using Amazon.S3;

namespace OSVFS.ObjectStore.S3;

/// <summary>
/// Wraps AWS S3 calls so that an <c>ExpiredToken</c> failure triggers a
/// single forced credential refresh and one retry. Belt-and-braces around
/// the SDK's own preempt-window refresh path: every
/// <see cref="RefreshingAWSCredentials"/> the SDK ships
/// (<see cref="Amazon.Runtime.AssumeRoleAWSCredentials"/>,
/// SSO, <see cref="ProcessAWSCredentials"/>, …) normally rolls the
/// credential over before its <see cref="AwsCredential.ExpiresAt"/>, but
/// wall-clock skew, machine sleep / resume, or a locally-cached credential
/// that turned out to be already-expired can still produce an
/// <c>ExpiredToken</c> response on the wire. The retry hides that race
/// from the caller and keeps the operation transparent.
///
/// <para>
/// On retry success a single Information line is emitted via
/// <see cref="ICredentialRefreshNotifier.NotifyRefreshed"/>; on retry
/// failure <see cref="ICredentialRefreshNotifier.NotifyRefreshFailure"/>
/// raises the operator-facing toast so they know to re-run
/// <c>aws configure sso</c> / <c>aws login</c> / <c>credentials set</c>.
/// </para>
/// </summary>
internal static class ExpiredTokenRetry
{
    /// <summary>
    /// AWS error code returned by S3 when a temporary credential's session
    /// token has elapsed. Distinct from the throttling / 5xx codes that the
    /// SDK's adaptive retry pipeline already handles.
    /// </summary>
    internal const string ExpiredTokenErrorCode = "ExpiredToken";

    /// <summary>
    /// Synonym used by some auth-backed services (notably STS itself); we
    /// treat it the same.
    /// </summary>
    internal const string ExpiredTokenExceptionErrorCode = "ExpiredTokenException";

    /// <summary>
    /// Runs <paramref name="action"/>; on an <c>ExpiredToken</c> response and
    /// when <paramref name="refreshable"/> is non-null, clears the cached
    /// credential and re-runs the action exactly once. Notifies
    /// <paramref name="notifier"/> on the retry outcome (success → log;
    /// failure → toast). Any other exception is propagated as-is so the
    /// caller's catch sites still observe the expected failure modes.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> action,
        RefreshingAWSCredentials? refreshable,
        ICredentialRefreshNotifier? notifier = null,
        string? sourceDescription = null)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (refreshable is not null && IsExpiredTokenError(ex))
        {
            refreshable.ClearCredentials();
            try
            {
                var result = await action().ConfigureAwait(false);
                notifier?.NotifyRefreshed(
                    sourceDescription ?? "(unknown)",
                    refreshable.Expiration is { } e
                        ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc), TimeSpan.Zero)
                        : null);
                return result;
            }
            catch (Exception inner)
            {
                notifier?.NotifyRefreshFailure(sourceDescription ?? "(unknown)", inner);
                throw;
            }
        }
    }

    /// <summary>
    /// <see cref="RunAsync{T}"/> for void-returning operations.
    /// </summary>
    public static async Task RunAsync(
        Func<Task> action,
        RefreshingAWSCredentials? refreshable,
        ICredentialRefreshNotifier? notifier = null,
        string? sourceDescription = null)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (refreshable is not null && IsExpiredTokenError(ex))
        {
            refreshable.ClearCredentials();
            try
            {
                await action().ConfigureAwait(false);
                notifier?.NotifyRefreshed(
                    sourceDescription ?? "(unknown)",
                    refreshable.Expiration is { } e
                        ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc), TimeSpan.Zero)
                        : null);
            }
            catch (Exception inner)
            {
                notifier?.NotifyRefreshFailure(sourceDescription ?? "(unknown)", inner);
                throw;
            }
        }
    }

    /// <summary>
    /// True for the S3-side expired-credential error codes; everything else
    /// (auth signature mismatch, transient network errors, throttling) flows
    /// through unchanged.
    /// </summary>
    internal static bool IsExpiredTokenError(AmazonS3Exception ex) =>
        string.Equals(ex.ErrorCode, ExpiredTokenErrorCode, StringComparison.Ordinal)
        || string.Equals(ex.ErrorCode, ExpiredTokenExceptionErrorCode, StringComparison.Ordinal);
}
