using Amazon.Runtime;
using Amazon.S3;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore.S3;

/// <summary>
/// Verifies the <see cref="ExpiredTokenRetry"/> wrapper:
/// • happy path passes results through;
/// • on <c>ExpiredToken</c> the supplied
///   <see cref="RefreshingAWSCredentials"/> is cleared and the action is
///   re-run exactly once;
/// • the notifier fires on retry success / failure;
/// • unrelated <see cref="AmazonS3Exception"/> codes flow through untouched;
/// • a null refreshable short-circuits with no retry.
/// </summary>
public class ExpiredTokenRetryTests
{
    [Fact]
    public async Task Happy_path_returns_action_result_without_clearing_credentials()
    {
        var refreshable = new RecordingRefreshingCredentials();
        var notifier = new RecordingNotifier();
        var calls = 0;

        var result = await ExpiredTokenRetry.RunAsync(
            () =>
            {
                calls++;
                return Task.FromResult(42);
            },
            refreshable,
            notifier,
            "test-source");

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
        Assert.Equal(0, refreshable.ClearCalls);
        Assert.Empty(notifier.Successes);
        Assert.Empty(notifier.Failures);
    }

    [Fact]
    public async Task ExpiredToken_clears_credentials_retries_once_and_notifies_success()
    {
        var refreshable = new RecordingRefreshingCredentials();
        var notifier = new RecordingNotifier();
        var attempts = 0;

        var result = await ExpiredTokenRetry.RunAsync(
            () =>
            {
                attempts++;
                if (attempts == 1) throw NewS3Exception("ExpiredToken");
                return Task.FromResult("ok");
            },
            refreshable,
            notifier,
            "profile 'demo'");

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, refreshable.ClearCalls);
        var success = Assert.Single(notifier.Successes);
        Assert.Equal("profile 'demo'", success.Source);
        Assert.Empty(notifier.Failures);
    }

    [Fact]
    public async Task ExpiredToken_then_failure_on_retry_notifies_failure_and_propagates()
    {
        var refreshable = new RecordingRefreshingCredentials();
        var notifier = new RecordingNotifier();
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            ExpiredTokenRetry.RunAsync(
                Task<int> () =>
                {
                    attempts++;
                    throw NewS3Exception("ExpiredToken");
                },
                refreshable,
                notifier,
                "profile 'demo'"));

        Assert.Equal("ExpiredToken", ex.ErrorCode);
        Assert.Equal(2, attempts);
        Assert.Equal(1, refreshable.ClearCalls);
        Assert.Empty(notifier.Successes);
        var failure = Assert.Single(notifier.Failures);
        Assert.Equal("profile 'demo'", failure.Source);
    }

    [Fact]
    public async Task Unrelated_S3_exception_codes_are_not_retried()
    {
        var refreshable = new RecordingRefreshingCredentials();
        var notifier = new RecordingNotifier();
        var attempts = 0;

        await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            ExpiredTokenRetry.RunAsync(
                Task<int> () =>
                {
                    attempts++;
                    throw NewS3Exception("AccessDenied");
                },
                refreshable,
                notifier,
                "profile 'demo'"));

        Assert.Equal(1, attempts);
        Assert.Equal(0, refreshable.ClearCalls);
        Assert.Empty(notifier.Failures);
    }

    [Fact]
    public async Task Without_refreshable_no_retry_happens()
    {
        var notifier = new RecordingNotifier();
        var attempts = 0;

        await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            ExpiredTokenRetry.RunAsync(
                Task<int> () =>
                {
                    attempts++;
                    throw NewS3Exception("ExpiredToken");
                },
                refreshable: null,
                notifier,
                "profile 'demo'"));

        Assert.Equal(1, attempts);
        Assert.Empty(notifier.Failures);
    }

    [Fact]
    public async Task ExpiredTokenException_synonym_triggers_the_same_retry_path()
    {
        var refreshable = new RecordingRefreshingCredentials();
        var notifier = new RecordingNotifier();
        var attempts = 0;

        var result = await ExpiredTokenRetry.RunAsync(
            () =>
            {
                attempts++;
                if (attempts == 1) throw NewS3Exception("ExpiredTokenException");
                return Task.FromResult(7);
            },
            refreshable,
            notifier,
            "profile 'demo'");

        Assert.Equal(7, result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, refreshable.ClearCalls);
        Assert.Single(notifier.Successes);
    }

    /// <summary>
    /// Builds a minimally-populated <see cref="AmazonS3Exception"/> with the
    /// requested error code. The constructor that accepts ErrorCode lives in
    /// AWSSDK v4; using it directly avoids reflection.
    /// </summary>
    private static AmazonS3Exception NewS3Exception(string errorCode) =>
        new(
            message: $"simulated {errorCode}",
            innerException: null,
            errorType: ErrorType.Sender,
            errorCode: errorCode,
            requestId: "req-id",
            statusCode: System.Net.HttpStatusCode.Forbidden);

    /// <summary>
    /// Test stub that satisfies <see cref="RefreshingAWSCredentials"/>'s
    /// abstract surface, exposing a counter for <c>ClearCredentials</c> calls
    /// so tests can verify the retry path forced a refresh.
    /// </summary>
    private sealed class RecordingRefreshingCredentials : RefreshingAWSCredentials
    {
        public int ClearCalls { get; private set; }

        protected override CredentialsRefreshState GenerateNewCredentials() => new()
        {
            Credentials = new ImmutableCredentials("AKIA-TEST", "secret-test", token: null),
            Expiration = DateTime.UtcNow.AddHours(1),
        };

        public override void ClearCredentials()
        {
            ClearCalls++;
            base.ClearCredentials();
        }
    }

    /// <summary>
    /// Captures every notifier callback so the test can assert call counts
    /// and the exact arguments threaded through.
    /// </summary>
    private sealed class RecordingNotifier : ICredentialRefreshNotifier
    {
        public List<(string Source, DateTimeOffset? NewExpiresAt)> Successes { get; } = [];

        public List<(string Source, Exception Error)> Failures { get; } = [];

        public void NotifyRefreshed(string source, DateTimeOffset? newExpiresAt) =>
            Successes.Add((source, newExpiresAt));

        public void NotifyRefreshFailure(string source, Exception error) =>
            Failures.Add((source, error));
    }
}
