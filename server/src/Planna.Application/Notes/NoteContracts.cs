using System.Text.Json.Serialization;

namespace Planna.Application.Notes;

public sealed record NoteView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("task_id")] Guid? TaskId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record NoteList([property: JsonPropertyName("items")] IReadOnlyList<NoteView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateNoteRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("task_id")] Guid? TaskId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NotePatch(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("content")] string? Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateNoteRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] NotePatch? Patch);

public enum NoteStatus
{
    Ok, Created, Updated, Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record NoteResult(NoteStatus Status, NoteView? Note = null, NoteList? Notes = null,
    string? Code = null, object? Details = null);

public interface INoteService
{
    Task<NoteResult> ListAsync(Guid ownerId, string? query, Guid? taskId, int limit,
        CancellationToken ct);

    Task<NoteResult> GetAsync(Guid ownerId, Guid noteId, CancellationToken ct);

    Task<NoteResult> CreateAsync(Guid ownerId, Guid mutationId, CreateNoteRequest request,
        CancellationToken ct);

    Task<NoteResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid noteId,
        UpdateNoteRequest request, CancellationToken ct);

    Task<NoteResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid noteId, long expectedVersion,
        CancellationToken ct);
}
