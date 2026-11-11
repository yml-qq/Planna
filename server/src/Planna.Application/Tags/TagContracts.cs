using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Tags;

public sealed record TagView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("version")] long Version);

public sealed record TagListView(
    [property: JsonPropertyName("items")] IReadOnlyList<TagView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTagRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string? Color);

[JsonConverter(typeof(TagPatchJsonConverter))]
public sealed class TagPatch
{
    public string? Name { get; set; }
    public string? Color { get; set; }

    [JsonIgnore]
    public IReadOnlySet<string> Provided => _provided;

    private readonly HashSet<string> _provided = [];

    internal void Mark(string name) => _provided.Add(name);
}

public sealed class TagPatchJsonConverter : JsonConverter<TagPatch>
{
    public override TagPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("patch must be an object.");
        var patch = new TagPatch();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return patch;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("patch must contain properties.");
            var name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "name":
                    if (reader.TokenType == JsonTokenType.Null) throw new JsonException("name cannot be null.");
                    patch.Name = reader.GetString();
                    break;
                case "color":
                    patch.Color = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    throw new JsonException($"unknown patch field '{name}'.");
            }
            patch.Mark(name);
        }
        throw new JsonException("unterminated patch object.");
    }

    public override void Write(Utf8JsonWriter writer, TagPatch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Provided.Contains("name")) writer.WriteString("name", value.Name);
        if (value.Provided.Contains("color"))
        {
            writer.WritePropertyName("color");
            if (value.Color is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Color);
        }
        writer.WriteEndObject();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateTagRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] TagPatch Patch);

public enum CreateTagStatus { Created, Replayed, Invalid, NameConflict, Conflict, AccountDeleted }

public sealed record CreateTagResult(CreateTagStatus Status, TagView? Tag = null, string? Code = null);

public interface ICreateTag
{
    Task<CreateTagResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateTagRequest request, CancellationToken ct);
}

public enum UpdateTagStatus { Updated, Replayed, Invalid, NotFound, VersionConflict, NameConflict, Conflict, AccountDeleted }

public sealed record UpdateTagResult(UpdateTagStatus Status, TagView? Tag = null,
    string? Code = null, TagConflictDetails? Details = null);

public sealed record TagConflictDetails(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("server_version")] long ServerVersion,
    [property: JsonPropertyName("changed_fields")] IReadOnlyList<string> ChangedFields);

public interface IUpdateTag
{
    Task<UpdateTagResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid tagId,
        UpdateTagRequest request, CancellationToken ct);
}

public enum DeleteTagStatus { Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record DeleteTagResult(DeleteTagStatus Status, string? Code = null,
    TagConflictDetails? Details = null);

public interface IDeleteTag
{
    Task<DeleteTagResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid tagId,
        long expectedVersion, CancellationToken ct);
}

public enum GetTagStatus { Ok, NotFound }

public sealed record GetTagResult(GetTagStatus Status, TagView? Tag = null);

public interface ITagReadQuery
{
    Task<IReadOnlyList<TagView>> ListAsync(Guid ownerId, CancellationToken ct);

    Task<GetTagResult> GetAsync(Guid ownerId, Guid tagId, CancellationToken ct);
}
