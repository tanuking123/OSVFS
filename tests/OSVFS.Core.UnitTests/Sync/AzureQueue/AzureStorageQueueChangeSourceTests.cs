using OSVFS.Sync.AzureQueue;
using Xunit;

namespace OSVFS.Core.UnitTests.Sync.AzureQueue;

/// <summary>
/// Unit tests for the Event Grid subject parser inside
/// <see cref="AzureStorageQueueChangeSource"/>. The full receive / parse /
/// yield loop is exercised end-to-end against Azurite in the integration
/// tests; here we just nail down the parser's contract.
/// </summary>
public class AzureStorageQueueChangeSourceTests
{
    [Theory]
    [InlineData("/blobServices/default/containers/my-container/blobs/dir/file.txt",
                "my-container", "dir/file.txt")]
    [InlineData("/blobServices/default/containers/my-container/blobs/top.txt",
                "my-container", "top.txt")]
    [InlineData("/blobServices/default/containers/my-container/blobs/with spaces and äöü/file.txt",
                "my-container", "with spaces and äöü/file.txt")]
    public void ParseSubject_returns_container_and_blob_for_well_formed_subjects(
        string subject, string expectedContainer, string expectedBlob)
    {
        var (container, blob) = AzureStorageQueueChangeSource.ParseSubject(subject);
        Assert.Equal(expectedContainer, container);
        Assert.Equal(expectedBlob, blob);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/something/else")]
    [InlineData("/blobServices/default/containers/")]
    [InlineData("/blobServices/default/containers/c/blobs/")]
    [InlineData("/blobServices/default/containers//blobs/x")]
    public void ParseSubject_returns_null_for_malformed_subjects(string? subject)
    {
        var (container, blob) = AzureStorageQueueChangeSource.ParseSubject(subject);
        Assert.Null(container);
        Assert.Null(blob);
    }
}
