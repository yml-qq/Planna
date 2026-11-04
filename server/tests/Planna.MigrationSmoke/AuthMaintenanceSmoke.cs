using Microsoft.EntityFrameworkCore;
using Npgsql;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;

internal static class AuthMaintenanceSmoke
{
    public static async Task RunAsync(NpgsqlConnection connection)
    {
        var oldChallenge = Guid.NewGuid();
        var freshChallenge = Guid.NewGuid();
        var waitingChallenge = Guid.NewGuid();
        await Execute(connection, """
            INSERT INTO auth_login_challenges(id,email,request_key,code_hash,created_at,expires_at)
            VALUES ($1,'old-maintenance@example.test',$2,'synthetic',now()-interval '25 hours',now()-interval '24 hours'),
                   ($3,'fresh-maintenance@example.test',$4,'synthetic',now()-interval '23 hours',now()-interval '22 hours'),
                   ($5,'waiting-maintenance@example.test',$6,'synthetic',now()-interval '3 minutes',now()+interval '7 minutes')
            """, oldChallenge, Guid.NewGuid(), freshChallenge, Guid.NewGuid(), waitingChallenge, Guid.NewGuid());
        await Execute(connection, """
            INSERT INTO auth_email_outbox(challenge_id,ciphertext,nonce,tag,next_attempt_at,created_at)
            VALUES ($1,decode('010203040506','hex'),decode('000000000000000000000000','hex'),
                decode('00000000000000000000000000000000','hex'),now()-interval '25 hours',now()-interval '25 hours'),
                   ($2,decode('010203040506','hex'),decode('000000000000000000000000','hex'),
                decode('00000000000000000000000000000000','hex'),now()-interval '3 minutes',now()-interval '3 minutes')
            """, oldChallenge, waitingChallenge);

        var owner = Guid.NewGuid();
        var revokedSession = Guid.NewGuid();
        var activeSession = Guid.NewGuid();
        var revokedHash = Guid.NewGuid().ToString("N");
        var activeHash = Guid.NewGuid().ToString("N");
        await Execute(connection, "INSERT INTO users(id,email) VALUES($1,'maintenance-owner@example.test')", owner);
        await Execute(connection, """
            INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash,revoked_at)
            VALUES ($1,$2,'Revoked','windows',$3,now()-interval '31 days'),
                   ($4,$2,'Active','windows',$5,NULL)
            """, revokedSession, owner, revokedHash, activeSession, activeHash);
        await Execute(connection, """
            INSERT INTO refresh_token_history(token_hash,session_id,consumed_at,created_at)
            VALUES ($1,$2,now()-interval '31 days',now()-interval '32 days'),
                   ($3,$4,now()-interval '31 days',now()-interval '32 days')
            """, revokedHash, revokedSession, activeHash, activeSession);

        var testConnection = Environment.GetEnvironmentVariable("PLANNADB_CONNECTION")
            ?? throw new InvalidOperationException("Isolated test connection is missing.");
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseNpgsql(testConnection).Options;
        await using var db = new AuthContext(options);
        var maintenance = new AuthQueueMaintenance(db, TimeProvider.System);
        var snapshot = await maintenance.RunOnceAsync(CancellationToken.None);
        if (snapshot.DeletedChallenges < 1 || snapshot.DeletedRevokedTokens < 1 ||
            !snapshot.BacklogAlert || snapshot.OldestWaitingSeconds < 170)
            throw new InvalidOperationException("Maintenance did not clean records or detect backlog.");
        if (await Scalar(connection, "SELECT count(*) FROM auth_login_challenges WHERE id=$1", oldChallenge) != 0 ||
            await Scalar(connection, "SELECT count(*) FROM auth_email_outbox WHERE challenge_id=$1", oldChallenge) != 0 ||
            await Scalar(connection, "SELECT count(*) FROM auth_login_challenges WHERE id=$1", freshChallenge) != 1 ||
            await Scalar(connection, "SELECT count(*) FROM refresh_token_history WHERE token_hash=$1", revokedHash) != 0 ||
            await Scalar(connection, "SELECT count(*) FROM refresh_token_history WHERE token_hash=$1", activeHash) != 1)
            throw new InvalidOperationException("Maintenance crossed a retention or replay-safety boundary.");

        await Execute(connection, """
            UPDATE auth_email_outbox SET status='sent',ciphertext=NULL,nonce=NULL,tag=NULL,sent_at=now()
            WHERE challenge_id=$1
            """, waitingChallenge);
        var second = await maintenance.RunOnceAsync(CancellationToken.None);
        if (second.DeletedChallenges != 0 || second.DeletedRevokedTokens != 0 || second.BacklogAlert)
            throw new InvalidOperationException("Maintenance was not idempotent or backlog did not clear.");
        Console.WriteLine("PASS: auth queue backlog signal, 24h challenge cleanup, revoked-only token cleanup");
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection connection, string sql, object value)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }
}
