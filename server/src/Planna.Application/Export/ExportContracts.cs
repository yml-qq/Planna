using System.Text.Json.Serialization;

namespace Planna.Application.Export;

public sealed record AccountExportJobView(
    [property: JsonPropertyName("export_id")] Guid ExportId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("snapshot_at")] DateTimeOffset? SnapshotAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

public sealed record AccountExportJobList(
    [property: JsonPropertyName("items")] IReadOnlyList<AccountExportJobView> Items);

public enum AccountExportStatus { Created, Listed, Ok, NotFound, NotReady, Expired, Replayed, Conflict, AccountDeleted }

public sealed record AccountExportResult(AccountExportStatus Status, AccountExportJobView? Job = null,
    AccountExportJobList? Jobs = null, string? Payload = null, string? Code = null);

public interface IAccountExportService
{
    Task<AccountExportResult> CreateAsync(Guid ownerId, Guid mutationId, CancellationToken ct);

    Task<AccountExportResult> ListAsync(Guid ownerId, CancellationToken ct);

    Task<AccountExportResult> GetAsync(Guid ownerId, Guid exportId, CancellationToken ct);

    Task<AccountExportResult> DownloadAsync(Guid ownerId, Guid exportId, CancellationToken ct);
}
