namespace OSVFS.Net;

/// <summary>
/// Optional upper bounds (in bytes per second) on object-store traffic. A null
/// component means "no limit on this direction".
/// </summary>
internal readonly record struct BandwidthLimits(long? UpBytesPerSecond, long? DownBytesPerSecond)
{
    /// <summary>
    /// True when at least one direction has a positive ceiling configured.
    /// </summary>
    public bool HasAnyLimit => UpBytesPerSecond is > 0 || DownBytesPerSecond is > 0;
}
