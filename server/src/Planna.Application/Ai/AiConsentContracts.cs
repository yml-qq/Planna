using System.Text.Json.Serialization;

namespace Planna.Application.Ai;

public static class AiConsentScopes
{
    public static readonly IReadOnlyList<string> Allowed =
        ["task_drafts", "plan_preview", "task_notes", "calendar_free_slots"];

    public static bool IsKnown(string scope) => Allowed.Contains(scope);
}

public sealed record AiConsentView(
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string> AllowedScopes,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAiConsentRequest(
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string>? AllowedScopes);

public enum AiConsentStatus { Ok, Updated, Invalid, Replayed, Conflict, AccountDeleted }

public sealed record AiConsentResult(AiConsentStatus Status, AiConsentView? View = null, string? Code = null);

public interface IAiConsent
{
    Task<AiConsentResult> GetAsync(Guid ownerId, CancellationToken ct);

    Task<AiConsentResult> UpdateAsync(Guid ownerId, Guid mutationId, UpdateAiConsentRequest request,
        CancellationToken ct);
}
