using System.Text.Json.Serialization;

namespace Planna.Application.Notes;

public sealed record NoteAttachmentView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("note_id")] Guid NoteId,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record NoteAttachmentList(
    [property: JsonPropertyName("items")] IReadOnlyList<NoteAttachmentView> Items);

public enum NoteAttachmentStatus
{
    Ok, Created, Replayed, Invalid, NotFound, TooLarge, UnsupportedType, RateLimited
}

public sealed record NoteAttachmentResult(NoteAttachmentStatus Status,
    NoteAttachmentView? Attachment = null, NoteAttachmentList? List = null, string? Code = null,
    int? RetryAfterSeconds = null);

public sealed record NoteAttachmentDownload(NoteAttachmentView Attachment, Stream Content);

public interface INoteAttachmentService
{
    Task<NoteAttachmentResult> ListAsync(Guid ownerId, Guid noteId, CancellationToken ct);

    Task<NoteAttachmentResult> UploadAsync(Guid ownerId, Guid noteId, Guid attachmentId,
        string? filename, string? contentType, long length, Stream content, CancellationToken ct);

    Task<(NoteAttachmentResult Result, NoteAttachmentDownload? Download)> OpenAsync(Guid ownerId,
        Guid noteId, Guid attachmentId, CancellationToken ct);

    Task<NoteAttachmentResult> DeleteAsync(Guid ownerId, Guid noteId, Guid attachmentId,
        CancellationToken ct);
}

public sealed record AttachmentCleanupReport(int PurgedAttachments, int RemovedFiles);

public interface INoteAttachmentCleanup
{
    Task<AttachmentCleanupReport> PurgeDeletedAsync(DateTimeOffset olderThan,
        CancellationToken ct);
}
