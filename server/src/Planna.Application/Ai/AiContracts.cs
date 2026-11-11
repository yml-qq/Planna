using System.Text.Json.Serialization;

namespace Planna.Application.Ai;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiParseRequest(
    [property: JsonPropertyName("input")] string? Input,
    [property: JsonPropertyName("context")] AiParseContext? Context,
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string>? AllowedScopes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiParseContext(
    [property: JsonPropertyName("time_zone")] string? TimeZone,
    [property: JsonPropertyName("now")] DateTimeOffset? Now);

public sealed record AiParseDraft(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("reminders")] IReadOnlyList<object> Reminders);

public sealed record AiParseResponse(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("drafts")] IReadOnlyList<AiParseDraft> Drafts,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions,
    [property: JsonPropertyName("unresolved_fields")] IReadOnlyList<string> UnresolvedFields);

public sealed record AiParseInput(string Text, DateTimeOffset Now, string TimeZone);

// Model providers (LLM or deterministic rule-based) implement this; they never receive SQL, shell or credentials.
public interface IAiParseProvider
{
    Task<AiParseResponse> ParseAsync(AiParseInput input, CancellationToken ct);
}
