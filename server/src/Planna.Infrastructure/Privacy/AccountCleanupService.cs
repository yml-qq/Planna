using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Planna.Infrastructure.Privacy;

// Retention cleanup for accepted account deletions: purges owned business data and keeps the
// tombstone user row plus the deletion request (audit) with completed_at recorded.
public sealed class AccountCleanupService(string connectionString, ILogger<AccountCleanupService> logger)
{
    public sealed record CleanupReport(int PurgedRequests, int FailedRequests);

    public async Task<CleanupReport> RunOnceAsync(DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var due = (await connection.QueryAsync<RequestRow>(new CommandDefinition("""
            SELECT r.id AS "Id", r.owner_id AS "OwnerId", u.email AS "Email"
            FROM account_deletion_requests AS r
            JOIN users AS u ON u.id = r.owner_id
            WHERE r.status='accepted' AND r.completed_at IS NULL AND r.requested_at <= @Cutoff
            ORDER BY r.requested_at
            LIMIT 100
            """, new { Cutoff = (now - retention).UtcDateTime }, cancellationToken: ct))).ToList();
        var purged = 0;
        var failed = 0;
        foreach (var request in due)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(PurgeSql,
                    new { Owner = request.OwnerId, Email = request.Email }, transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE account_deletion_requests
                    SET completed_at=@Now, last_error_code=NULL
                    WHERE id=@Id
                    """, new { Now = now.UtcDateTime, request.Id }, transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                purged++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await transaction.RollbackAsync(ct);
                failed++;
                logger.LogError(ex, "PRIVACY_CLEANUP_FAILED deletion_request_id={RequestId}", request.Id);
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE account_deletion_requests SET last_error_code='CLEANUP_FAILED'
                    WHERE id=@Id
                    """, new { request.Id }, cancellationToken: ct));
            }
        }
        if (due.Count > 0)
            logger.LogInformation("PRIVACY_CLEANUP_RUN candidates={Candidates} purged={Purged} failed={Failed}",
                due.Count, purged, failed);
        return new CleanupReport(purged, failed);
    }

    // Order respects foreign keys between tasks/steps/occurrences/tags/reminders/time blocks.
    private const string PurgeSql = """
        DELETE FROM ai_action_audits WHERE owner_id=@Owner;
        DELETE FROM ai_plans WHERE owner_id=@Owner;
        DELETE FROM ai_breakdown_drafts WHERE owner_id=@Owner;
        DELETE FROM task_occurrences WHERE owner_id=@Owner;
        DELETE FROM reminders WHERE owner_id=@Owner;
        DELETE FROM task_time_blocks WHERE owner_id=@Owner;
        DELETE FROM task_steps WHERE owner_id=@Owner;
        DELETE FROM recurrences WHERE owner_id=@Owner;
        DELETE FROM task_tags WHERE owner_id=@Owner;
        DELETE FROM tasks WHERE owner_id=@Owner;
        DELETE FROM task_lists WHERE owner_id=@Owner;
        DELETE FROM tags WHERE owner_id=@Owner;
        DELETE FROM sync_changes WHERE owner_id=@Owner;
        DELETE FROM client_mutations WHERE owner_id=@Owner;
        DELETE FROM account_exports WHERE owner_id=@Owner;
        DELETE FROM ai_consents WHERE owner_id=@Owner;
        DELETE FROM auth_step_up_email_outbox
            WHERE challenge_id IN (SELECT id FROM auth_step_up_challenges WHERE user_id=@Owner);
        DELETE FROM auth_step_up_challenges WHERE user_id=@Owner;
        DELETE FROM auth_email_outbox
            WHERE challenge_id IN (SELECT id FROM auth_login_challenges WHERE email=@Email);
        DELETE FROM auth_login_challenges WHERE email=@Email;
        DELETE FROM refresh_token_history
            WHERE session_id IN (SELECT id FROM device_sessions WHERE user_id=@Owner);
        DELETE FROM device_sessions WHERE user_id=@Owner;
        """;

    private sealed class RequestRow
    {
        public Guid Id { get; set; }
        public Guid OwnerId { get; set; }
        public string Email { get; set; } = "";
    }
}
