using System.Text.Json.Serialization;

namespace OSVFS.Sync.AzureQueue;

/// <summary>
/// Parsed shape of an Event Grid Blob notification delivered through an Azure
/// Storage Queue. Only the fields the change source actually consumes are
/// modeled; unknown JSON members are tolerated.
/// </summary>
/// <remarks>
/// Reference: <see href="https://learn.microsoft.com/azure/storage/blobs/storage-blob-event-overview"/>.
/// Event Grid wraps the storage event payload with <c>topic</c>,
/// <c>eventType</c>, <c>subject</c>, and <c>data</c> fields. The change
/// source recognizes <c>Microsoft.Storage.BlobCreated</c> and
/// <c>Microsoft.Storage.BlobDeleted</c>; everything else is dropped.
/// </remarks>
internal sealed record EventGridBlobEvent(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("data")] EventGridBlobEventData? Data);

/// <summary>
/// Inner <c>data</c> payload carrying blob identity. <c>contentLength</c> and
/// <c>eTag</c> are populated for create-style events; deletes leave them null.
/// </summary>
internal sealed record EventGridBlobEventData(
    [property: JsonPropertyName("api")] string? Api,
    [property: JsonPropertyName("contentLength")] long? ContentLength,
    [property: JsonPropertyName("eTag")] string? ETag,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>
/// Source-generated <see cref="System.Text.Json.JsonSerializerContext"/> so the
/// Event Grid payload deserializes without runtime reflection (Native AOT safe).
/// </summary>
[JsonSerializable(typeof(EventGridBlobEvent))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class EventGridBlobEventJsonContext : JsonSerializerContext
{
}
