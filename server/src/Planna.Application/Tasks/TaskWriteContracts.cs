using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

// Patch fields that were present in the request keep their client values;
// a present null clears a nullable field. Absent fields are untouched.
[JsonConverter(typeof(TaskPatchJsonConverter))]
public sealed class TaskPatch
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public short? Priority { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateOnly? DueLocalDate { get; set; }
    public string? TimeZone { get; set; }
    public short? EstimateMinutes { get; set; }

    [JsonIgnore]
    public IReadOnlySet<string> Provided => _provided;

    private readonly HashSet<string> _provided = [];

    internal void Mark(string name) => _provided.Add(name);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateTaskRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] TaskPatch Patch);

public sealed class TaskPatchJsonConverter : JsonConverter<TaskPatch>
{
    public override TaskPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("patch must be an object.");
        var patch = new TaskPatch();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return patch;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("patch must contain properties.");
            var name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "title":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("title cannot be null.");
                    patch.Title = reader.GetString();
                    break;
                case "description":
                    patch.Description = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "priority":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("priority cannot be null.");
                    patch.Priority = reader.GetInt16();
                    break;
                case "due_at":
                    patch.DueAt = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();
                    break;
                case "due_local_date":
                    patch.DueLocalDate = reader.TokenType == JsonTokenType.Null
                        ? null : DateOnly.Parse(reader.GetString()!);
                    break;
                case "time_zone":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("time_zone cannot be null.");
                    patch.TimeZone = reader.GetString();
                    break;
                case "estimate_minutes":
                    patch.EstimateMinutes = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt16();
                    break;
                default:
                    throw new JsonException($"unknown patch field '{name}'.");
            }
            patch.Mark(name);
        }
        throw new JsonException("unterminated patch object.");
    }

    public override void Write(Utf8JsonWriter writer, TaskPatch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, write) in Fields)
            if (value.Provided.Contains(name))
            {
                writer.WritePropertyName(name);
                write(writer, value);
            }
        writer.WriteEndObject();
    }

    private static readonly (string Name, Action<Utf8JsonWriter, TaskPatch> Write)[] Fields =
    [
        ("title", (w, p) => w.WriteStringValue(p.Title)),
        ("description", (w, p) => WriteNullable(w, p.Description)),
        ("priority", (w, p) => w.WriteNumberValue(p.Priority!.Value)),
        ("due_at", (w, p) => WriteNullable(w, p.DueAt)),
        ("due_local_date", (w, p) => WriteNullable(w, p.DueLocalDate)),
        ("time_zone", (w, p) => w.WriteStringValue(p.TimeZone)),
        ("estimate_minutes", (w, p) => WriteNullable(w, p.EstimateMinutes))
    ];

    private static void WriteNullable(Utf8JsonWriter writer, string? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    private static void WriteNullable(Utf8JsonWriter writer, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }

    private static void WriteNullable(Utf8JsonWriter writer, DateOnly? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void WriteNullable(Utf8JsonWriter writer, short? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

public enum UpdateTaskStatus { Updated, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record VersionConflictDetails(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("server_version")] long ServerVersion,
    [property: JsonPropertyName("changed_fields")] IReadOnlyList<string> ChangedFields);

public sealed record UpdateTaskResult(UpdateTaskStatus Status, TaskReadView? Task = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IUpdateTask
{
    Task<UpdateTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        UpdateTaskRequest request, CancellationToken ct);
}

public enum DeleteTaskStatus { Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record DeleteTaskResult(DeleteTaskStatus Status, string? Code = null,
    VersionConflictDetails? Details = null);

public interface IDeleteTask
{
    Task<DeleteTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        long expectedVersion, CancellationToken ct);
}
