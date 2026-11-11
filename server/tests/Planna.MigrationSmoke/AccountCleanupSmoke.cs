using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planna.Infrastructure.Privacy;

internal static class AccountCleanupSmoke
{
    public static async Task RunAsync(NpgsqlConnection db, string connectionString)
    {
        var oldOwner = Guid.NewGuid();
        var recentOwner = Guid.NewGuid();
        var boundaryOwner = Guid.NewGuid();
        var oldEmail = "privacy-cleanup-old@example.test";
        var recentEmail = "privacy-cleanup-recent@example.test";
        var boundaryEmail = "privacy-cleanup-boundary@example.test";
        var oldRequest = Guid.NewGuid();
        var recentRequest = Guid.NewGuid();
        var boundaryRequest = Guid.NewGuid();
        var oldList = Guid.NewGuid();
        var recentList = Guid.NewGuid();
        var oldTask = Guid.NewGuid();
        var recentTask = Guid.NewGuid();
        var oldSession = Guid.NewGuid();
        var oldTag = Guid.NewGuid();
        var oldHabit = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email,deleted_at) VALUES($1,$2,now()-interval '40 days'),($3,$4,now()),($5,$6,now())",
            oldOwner, oldEmail, recentOwner, recentEmail, boundaryOwner, boundaryEmail);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2),($3)", oldOwner, recentOwner, boundaryOwner);
        await Exec(db, """
            INSERT INTO account_deletion_requests(id,owner_id,status,policy_version,requested_at)
            VALUES($1,$2,'accepted','account-deletion-v1',now()-interval '40 days'),
                  ($3,$4,'accepted','account-deletion-v1',now()),
                  ($5,$6,'accepted','account-deletion-v1',now()-interval '30 days')
            """, oldRequest, oldOwner, recentRequest, recentOwner, boundaryRequest, boundaryOwner);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'Old','custom'),($3,$4,'Recent','custom')",
            oldList, oldOwner, recentList, recentOwner);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Old task'),($4,$5,$6,'Recent task')",
            oldTask, oldOwner, oldList, recentTask, recentOwner, recentList);
        await Exec(db, "INSERT INTO habits(id,owner_id,title,schedule_type,time_zone) VALUES($1,$2,'Old habit','daily','Asia/Shanghai')",
            oldHabit, oldOwner);
        await Exec(db, "INSERT INTO habit_logs(owner_id,habit_id,log_date,quantity,status) VALUES($1,$2,'2026-11-01',1,'done')",
            oldOwner, oldHabit);
        await Exec(db, "INSERT INTO task_steps(id,owner_id,task_id,title) VALUES($1,$2,$3,'Step')", Guid.NewGuid(), oldOwner, oldTask);
        await Exec(db, "INSERT INTO tags(id,owner_id,name) VALUES($1,$2,'Tag')", oldTag, oldOwner);
        await Exec(db, "INSERT INTO task_tags(owner_id,task_id,tag_id) VALUES($1,$2,$3)", oldOwner, oldTask, oldTag);
        await Exec(db, "INSERT INTO reminders(id,owner_id,task_id,scheduled_at) VALUES($1,$2,$3,now())",
            Guid.NewGuid(), oldOwner, oldTask);
        await Exec(db, "INSERT INTO task_time_blocks(id,owner_id,task_id,start_at,end_at) VALUES($1,$2,$3,now(),now()+interval '30 minutes')",
            Guid.NewGuid(), oldOwner, oldTask);
        await Exec(db, "INSERT INTO sync_changes(owner_id,revision,entity_type,entity_id,op,entity_version) VALUES($1,1,'task',$2,'upsert',1)",
            oldOwner, oldTask);
        await Exec(db, "INSERT INTO client_mutations(owner_id,mutation_id,request_hash,response) VALUES($1,$2,'h','{}'::jsonb)",
            oldOwner, Guid.NewGuid());
        await Exec(db, "INSERT INTO ai_consents(owner_id,allowed_scopes) VALUES($1,ARRAY['task_drafts'])", oldOwner);
        await Exec(db, "INSERT INTO ai_action_audits(owner_id,action_type,target_id,actor) VALUES($1,'breakdown_apply',$2,'ai_confirmed')",
            oldOwner, oldTask);
        await Exec(db, "INSERT INTO account_exports(id,owner_id,status,requested_at) VALUES($1,$2,'pending',now())",
            Guid.NewGuid(), oldOwner);
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Old','windows','hash')",
            oldSession, oldOwner);
        await Exec(db, "INSERT INTO refresh_token_history(token_hash,session_id) VALUES('old-token',$1)", oldSession);
        await Exec(db, "INSERT INTO auth_login_challenges(email,code_hash,request_key,expires_at) VALUES($1,'h',$2,now()+interval '10 minutes')",
            oldEmail, Guid.NewGuid());
        await Exec(db, "INSERT INTO auth_step_up_challenges(user_id,request_key,code_hash,expires_at) VALUES($1,$2,'h',now()+interval '10 minutes')",
            oldOwner, Guid.NewGuid());

        var now = DateTimeOffset.UtcNow;
        var service = new AccountCleanupService(connectionString, NullLogger<AccountCleanupService>.Instance);
        var report = await service.RunOnceAsync(now, TimeSpan.FromDays(30), CancellationToken.None);
        if (report.PurgedRequests != 2 || report.FailedRequests != 0)
            throw new InvalidOperationException($"Unexpected cleanup report {report}.");
        if (await Scalar(db, "SELECT count(*) FROM tasks WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM task_lists WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM tags WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM task_tags WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM reminders WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM task_time_blocks WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM ai_consents WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM ai_action_audits WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM account_exports WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM device_sessions WHERE user_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM refresh_token_history WHERE token_hash='old-token'") != 0 ||
            await Scalar(db, "SELECT count(*) FROM auth_login_challenges WHERE email=$1", oldEmail) != 0 ||
            await Scalar(db, "SELECT count(*) FROM auth_step_up_challenges WHERE user_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM habits WHERE owner_id=$1", oldOwner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM habit_logs WHERE owner_id=$1", oldOwner) != 0)
            throw new InvalidOperationException("Retention cleanup left owner data behind.");
        if (await Scalar(db, "SELECT count(*) FROM users WHERE id=$1 AND deleted_at IS NOT NULL", oldOwner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM account_deletion_requests WHERE id=$1 AND completed_at IS NOT NULL AND last_error_code IS NULL", oldRequest) != 1 ||
            await Scalar(db, "SELECT count(*) FROM account_deletion_requests WHERE id=$1 AND completed_at IS NOT NULL", boundaryRequest) != 1 ||
            await Scalar(db, "SELECT count(*) FROM account_deletion_requests WHERE id=$1 AND completed_at IS NULL", recentRequest) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE owner_id=$1", recentOwner) != 1)
            throw new InvalidOperationException("Retention cleanup lost the tombstone/audit or touched recent accounts.");

        var second = await service.RunOnceAsync(now, TimeSpan.FromDays(30), CancellationToken.None);
        if (second.PurgedRequests != 0 || second.FailedRequests != 0)
            throw new InvalidOperationException("Retention cleanup was not idempotent.");
        Console.WriteLine("PASS: deletion retention cleanup purges owner data, keeps tombstone and audit, skips recent accounts");
    }

    private static async Task<long> Scalar(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private static async Task Exec(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
}
