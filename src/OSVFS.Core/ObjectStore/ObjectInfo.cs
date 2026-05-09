namespace OSVFS.ObjectStore;

/// <summary>
/// Snapshot of an object (or synthesized common-prefix directory) as it appears to the
/// virtualization layer. Provider-neutral so a future GCS or Azure Blob backend can
/// surface its own metadata through the same shape.
/// </summary>
/// <param name="Key">Virt-root-relative object key (linked prefix already stripped, forward-slash separated).</param>
/// <param name="RelativePath">Same key in Windows path form (backslash-separated).</param>
/// <param name="Size">Object size in bytes; zero for synthesized directories.</param>
/// <param name="LastModified">Last-modified timestamp; default for synthesized directories.</param>
/// <param name="ETag">Provider-supplied entity tag (HTTP ETag style; surrounding quotes preserved when present); empty for directories.</param>
/// <param name="IsDirectory">True when the entry represents a common prefix rather than a real object.</param>
/// <param name="UserMetadata">User-defined metadata (S3 <c>x-amz-meta-*</c> headers) keyed by lowercased name without the provider prefix; null when unknown (e.g. List responses) or absent.</param>
internal readonly record struct ObjectInfo(
    string Key,
    string RelativePath,
    long Size,
    DateTimeOffset LastModified,
    string ETag,
    bool IsDirectory,
    IReadOnlyDictionary<string, string>? UserMetadata = null);
