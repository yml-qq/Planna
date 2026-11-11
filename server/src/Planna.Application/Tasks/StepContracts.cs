using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

public sealed record StepView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("is_done")] bool IsDone,
    [property: JsonPropertyName("position")] decimal Position,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes,
    [property: JsonPropertyName("version")] long Version);

public sealed record StepListView(
    [property: JsonPropertyName("items")] IReadOnlyList<StepView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateStepRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("position")] decimal? Position,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes);

[JsonConverter(typeof(StepPatchJsonConverter))]
public sealed class StepPatch
{
    public string? Title { get; set; }
    public bool? IsDone { get; set; }
    public decimal? Position { get; set; }
    public short? EstimateMinutes { get; set; }

    [JsonIgnore]
    public IReadOnlySet<string> Provided => _provided;

    private readonly HashSet<string> _provided = [];

    internal void Mark(string name) => _provided.Add(name);
}

public sealed class StepPatchJsonConverter : JsonConverter<StepPatch>
{
    public override StepPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("patch must be an object.");
        var patch = new StepPatch();
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
                case "is_done":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("is_done cannot be null.");
                    patch.IsDone = reader.GetBoolean();
                    break;
                case "position":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("position cannot be null.");
                    patch.Position = reader.GetDecimal();
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

    public override void Write(Utf8JsonWriter writer, StepPatch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Provided.Contains("title")) writer.WriteString("title", value.Title);
        if (value.Provided.Contains("is_done")) writer.WriteBoolean("is_done", value.IsDone!.Value);
        if (value.Provided.Contains("position")) writer.WriteNumber("position", value.Position!.Value);
        if (value.Provided.Contains("estimate_minutes"))
        {
            writer.WritePropertyName("estimate_minutes");
            if (value.EstimateMinutes is null) writer.WriteNullValue();
            else writer.WriteNumberValue(value.EstimateMinutes.Value);
        }
        writer.WriteEndObject();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateStepRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] StepPatch Patch);

public enum CreateStepStatus { Created, Replayed, Invalid, TaskNotFound, Conflict, AccountDeleted }

public sealed record CreateStepResult(CreateStepStatus Status, StepView? Step = null, string? Code = null);

public interface ICreateStep
{
    Task<CreateStepResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateStepRequest request, CancellationToken ct);
}

public enum UpdateStepStatus { Updated, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record UpdateStepResult(UpdateStepStatus Status, StepView? Step = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IUpdateStep
{
    Task<UpdateStepResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid stepId,
        UpdateStepRequest request, CancellationToken ct);
}

public enum DeleteStepStatus { Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record DeleteStepResult(DeleteStepStatus Status, string? Code = null,
    VersionConflictDetails? Details = null);

public interface IDeleteStep
{
    Task<DeleteStepResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid stepId,
        long expectedVersion, CancellationToken ct);
}

public enum GetStepListStatus { Ok, TaskNotFound }

public sealed record GetStepListResult(GetStepListStatus Status, IReadOnlyList<StepView>? Items = null);

public interface IStepReadQuery
{
    Task<GetStepListResult> ListAsync(Guid ownerId, Guid taskId, CancellationToken ct);
}
