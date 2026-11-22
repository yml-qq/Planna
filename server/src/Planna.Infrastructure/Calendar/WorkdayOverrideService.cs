using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Calendar;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Calendar;

// Owner-scoped workday overrides. No legal holiday data is bundled: clients import verified
// calendars explicitly and every row is scoped to its owner and jurisdiction.
public sealed class WorkdayOverrideService(TaskWriteContext db, TimeProvider clock)
    : IWorkdayOverrideService
{
    private static readonly string[] Kinds = ["holiday", "workday"];

    public async Task<WorkdayOverrideResult> ListAsync(Guid ownerId, string? jurisdiction,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = NormalizeJurisdiction(jurisdiction);
        if (zone is null || from > to || to.DayNumber - from.DayNumber > 366)
            return new(WorkdayOverrideStatus.Invalid, Code: "INVALID_RANGE");
        var rows = await db.Database.GetDbConnection().QueryAsync<OverrideRow>(
            new CommandDefinition("""
                SELECT date AS "Date", jurisdiction AS "Jurisdiction", kind AS "Kind",
                    note AS "Note", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
                FROM workday_overrides
                WHERE owner_id=@Owner AND jurisdiction=@Jurisdiction
                    AND date >= @From AND date <= @To
                ORDER BY date
                """, new { Owner = ownerId, Jurisdiction = zone, From = from, To = to },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(WorkdayOverrideStatus.Ok,
            List: new WorkdayOverrideList(rows.Select(ToView).ToList()));
    }

    public async Task<WorkdayOverrideResult> UpsertAsync(Guid ownerId, DateOnly date,
        UpsertWorkdayOverrideRequest request, CancellationToken ct)
    {
        var zone = NormalizeJurisdiction(request.Jurisdiction);
        var kind = request.Kind?.Trim().ToLowerInvariant();
        var note = (request.Note ?? "").Trim();
        if (zone is null || kind is null || !Kinds.Contains(kind) || note.Length > 200)
            return new(WorkdayOverrideStatus.Invalid, Code: "INVALID_OVERRIDE");
        var existing = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<OverrideRow>(
            new CommandDefinition("""
                SELECT date AS "Date", jurisdiction AS "Jurisdiction", kind AS "Kind",
                    note AS "Note", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
                FROM workday_overrides
                WHERE owner_id=@Owner AND jurisdiction=@Jurisdiction AND date=@Date
                """, new { Owner = ownerId, Jurisdiction = zone, Date = date },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        var now = clock.GetUtcNow();
        if (existing is null)
        {
            await ExecuteAsync("""
                INSERT INTO workday_overrides(owner_id, jurisdiction, date, kind, note, created_at, updated_at)
                VALUES(@Owner, @Jurisdiction, @Date, @Kind, @Note, @Now, @Now)
                """, p =>
            {
                p.Add(new NpgsqlParameter("Owner", ownerId));
                p.Add(new NpgsqlParameter("Jurisdiction", zone));
                p.Add(new NpgsqlParameter("Date", NpgsqlDbType.Date) { Value = date });
                p.Add(new NpgsqlParameter("Kind", kind));
                p.Add(new NpgsqlParameter("Note", note));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            }, ct);
            return new(WorkdayOverrideStatus.Created, new WorkdayOverrideView(Format(date), zone,
                kind, note, now, now));
        }

        await ExecuteAsync("""
            UPDATE workday_overrides SET kind=@Kind, note=@Note, updated_at=@Now
            WHERE owner_id=@Owner AND jurisdiction=@Jurisdiction AND date=@Date
            """, p =>
        {
            p.Add(new NpgsqlParameter("Kind", kind));
            p.Add(new NpgsqlParameter("Note", note));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Jurisdiction", zone));
            p.Add(new NpgsqlParameter("Date", NpgsqlDbType.Date) { Value = date });
        }, ct);
        return new(WorkdayOverrideStatus.Updated, new WorkdayOverrideView(Format(date), zone, kind,
            note, Instant(existing.CreatedAt), now));
    }

    public async Task<WorkdayOverrideResult> DeleteAsync(Guid ownerId, DateOnly date,
        string? jurisdiction, CancellationToken ct)
    {
        var zone = NormalizeJurisdiction(jurisdiction);
        if (zone is null) return new(WorkdayOverrideStatus.Invalid, Code: "INVALID_JURISDICTION");
        var affected = await ExecuteAsync("""
            DELETE FROM workday_overrides
            WHERE owner_id=@Owner AND jurisdiction=@Jurisdiction AND date=@Date
            """, p =>
        {
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Jurisdiction", zone));
            p.Add(new NpgsqlParameter("Date", NpgsqlDbType.Date) { Value = date });
        }, ct);
        return affected == 0
            ? new(WorkdayOverrideStatus.NotFound, Code: "OVERRIDE_NOT_FOUND")
            : new(WorkdayOverrideStatus.Ok);
    }

    private async Task<int> ExecuteAsync(string sql, Action<DbParameterCollection> addParameters,
        CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static string? NormalizeJurisdiction(string? value)
    {
        var zone = string.IsNullOrWhiteSpace(value) ? "CN" : value.Trim().ToUpperInvariant();
        return zone.Length is < 1 or > 32 ? null : zone;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd");

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static WorkdayOverrideView ToView(OverrideRow row) => new(Format(row.Date),
        row.Jurisdiction, row.Kind, row.Note, Instant(row.CreatedAt), Instant(row.UpdatedAt));

    private sealed class OverrideRow
    {
        public DateOnly Date { get; set; }
        public string Jurisdiction { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Note { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
