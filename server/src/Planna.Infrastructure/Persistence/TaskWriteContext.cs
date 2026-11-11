using Microsoft.EntityFrameworkCore;

namespace Planna.Infrastructure.Persistence;

public sealed class TaskWriteContext(DbContextOptions<TaskWriteContext> options) : DbContext(options)
{
    public DbSet<TaskRow> Tasks => Set<TaskRow>();
    public DbSet<TaskListRow> Lists => Set<TaskListRow>();
    public DbSet<MutationRow> Mutations => Set<MutationRow>();
    public DbSet<SyncChangeRow> Changes => Set<SyncChangeRow>();
    public DbSet<TagRow> Tags => Set<TagRow>();
    public DbSet<StepRow> Steps => Set<StepRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TaskRow>(e =>
        {
            e.ToTable("tasks", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.ListId).HasColumnName("list_id");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.StartAt).HasColumnName("start_at");
            e.Property(x => x.DueAt).HasColumnName("due_at");
            e.Property(x => x.DueLocalDate).HasColumnName("due_local_date");
            e.Property(x => x.AllDay).HasColumnName("all_day");
            e.Property(x => x.TimeZone).HasColumnName("time_zone");
            e.Property(x => x.EstimateMinutes).HasColumnName("estimate_minutes");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Property(x => x.Position).HasColumnName("position");
        });
        model.Entity<TaskListRow>(e =>
        {
            e.ToTable("task_lists", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });
        model.Entity<MutationRow>(e =>
        {
            e.ToTable("client_mutations", t => t.ExcludeFromMigrations());
            e.HasKey(x => new { x.OwnerId, x.MutationId });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.MutationId).HasColumnName("mutation_id");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.Property(x => x.Response).HasColumnName("response").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<SyncChangeRow>(e =>
        {
            e.ToTable("sync_changes", t => t.ExcludeFromMigrations());
            e.HasKey(x => new { x.OwnerId, x.Revision, x.ChangeIndex });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.ChangeIndex).HasColumnName("change_index");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Op).HasColumnName("op");
            e.Property(x => x.EntityVersion).HasColumnName("entity_version");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<TagRow>(e =>
        {
            e.ToTable("tags", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });
        model.Entity<StepRow>(e =>
        {
            e.ToTable("task_steps", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.IsDone).HasColumnName("is_done");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.EstimateMinutes).HasColumnName("estimate_minutes");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });
    }
}

public sealed class TaskRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ListId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "todo";
    public short Priority { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateOnly? DueLocalDate { get; set; }
    public bool AllDay { get; set; }
    public string TimeZone { get; set; } = "Asia/Shanghai";
    public short? EstimateMinutes { get; set; }
    public decimal Position { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class TaskListRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "custom";
    public long Version { get; set; } = 1;
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class MutationRow
{
    public Guid OwnerId { get; set; }
    public Guid MutationId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SyncChangeRow
{
    public Guid OwnerId { get; set; }
    public long Revision { get; set; }
    public int ChangeIndex { get; set; }
    public string EntityType { get; set; } = "task";
    public Guid EntityId { get; set; }
    public string Op { get; set; } = "upsert";
    public long EntityVersion { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TagRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class StepRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid TaskId { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
    public decimal Position { get; set; }
    public short? EstimateMinutes { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
}
