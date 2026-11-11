using Dapper;
using Npgsql;
using Planna.Application.Tags;

namespace Planna.Infrastructure.Tags;

// Read model only. Tag writes stay in the EF Core tag services.
public sealed class TagQuery(string connectionString) : ITagReadQuery
{
    private const string Columns = "id AS \"Id\", name AS \"Name\", color AS \"Color\", version AS \"Version\"";

    public async Task<IReadOnlyList<TagView>> ListAsync(Guid ownerId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<TagDbRow>(new CommandDefinition(
            $"SELECT {Columns} FROM tags WHERE owner_id = @OwnerId AND deleted_at IS NULL ORDER BY lower(name), id",
            new { OwnerId = ownerId }, cancellationToken: ct));
        return rows.Select(ToView).ToList();
    }

    public async Task<GetTagResult> GetAsync(Guid ownerId, Guid tagId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TagDbRow>(new CommandDefinition(
            $"SELECT {Columns} FROM tags WHERE id = @Id AND owner_id = @OwnerId AND deleted_at IS NULL LIMIT 1",
            new { Id = tagId, OwnerId = ownerId }, cancellationToken: ct));
        return row is null
            ? new(GetTagStatus.NotFound)
            : new(GetTagStatus.Ok, ToView(row));
    }

    private static TagView ToView(TagDbRow row) => new(row.Id, row.Name, row.Color, row.Version);

    private sealed class TagDbRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public long Version { get; set; }
    }
}
