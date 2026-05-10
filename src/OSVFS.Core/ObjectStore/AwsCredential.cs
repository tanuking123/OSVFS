namespace OSVFS.ObjectStore;

/// <summary>
/// Static AWS credentials supplied to the S3 backend, bypassing the SDK's
/// default credential resolution chain.
/// </summary>
internal sealed record AwsCredential
{
    /// <summary>
    /// AWS access key ID (the public half of the credential pair).
    /// </summary>
    public required string AccessKeyId { get; init; }

    /// <summary>
    /// AWS secret access key (the secret half of the credential pair).
    /// </summary>
    public required string SecretAccessKey { get; init; }

    /// <summary>
    /// Optional session token for temporary credentials issued by STS.
    /// </summary>
    public string? SessionToken { get; init; }

    /// <summary>
    /// Wall-clock expiration of the credential pair. Populated for STS / SSO
    /// short-lived credentials so the SDK's <see cref="Amazon.Runtime.RefreshingAWSCredentials"/>
    /// can proactively refresh before hard expiry. Null for permanent IAM keys
    /// (which never expire).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
