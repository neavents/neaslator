using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Neaslator.Persistence;

public static class MigrationLockExtensions
{
    private const int LockWaitTimeoutSeconds = 240;

    public static async Task MigrateWithAdvisoryLockAsync(
        this DbContext context,
        PostgresDirect direct,
        string lockName,
        Func<CancellationToken, Task>? afterMigration = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName, nameof(lockName));

        PostgresDirectDiagnostics diagnostics =
            await direct.ValidateAsync(cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "Advisory locks will be taken on database {Database} (backend {BackendPid}); {SettingName} is {State}.",
            diagnostics.Database,
            diagnostics.BackendProcessId,
            PostgresDirect.SettingName,
            diagnostics.IsExplicitlyConfigured ? "configured" : "not set, reusing the pooled connection string");

        long lockKey = DeriveLockKey(lockName);

        await using NpgsqlConnection gate = await direct.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ScalarAsync(gate, "SELECT pg_advisory_lock(@key)", lockKey, LockWaitTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        bool released;

        try
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            if (afterMigration is not null)
            {
                await afterMigration(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            released = await TryReleaseAsync(gate, lockKey).ConfigureAwait(false);
        }

        if (!released)
        {
            logger?.LogError(
                "pg_advisory_unlock({LockKey}) for '{LockName}' released nothing. The migration lock has " +
                "leaked onto a server connection and will block other replicas until it is recycled. " +
                "{SettingName} is not reaching PostgreSQL directly.",
                lockKey,
                lockName,
                PostgresDirect.SettingName);
        }
    }

    private static async Task<bool> TryReleaseAsync(NpgsqlConnection gate, long lockKey)
    {
        try
        {
            return await ScalarAsync(
                gate, "SELECT pg_advisory_unlock(@key)", lockKey, timeoutSeconds: null, CancellationToken.None)
                .ConfigureAwait(false) is true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection gate,
        string sql,
        long lockKey,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = gate.CreateCommand();

        command.CommandText = sql;
        command.Parameters.AddWithValue("key", lockKey);

        if (timeoutSeconds is not null)
        {
            command.CommandTimeout = timeoutSeconds.Value;
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> TryRunWithAdvisoryLockAsync(
        this DbContext context,
        PostgresDirect? direct,
        string lockName,
        Func<CancellationToken, Task> work,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName, nameof(lockName));

        if (!context.Database.IsRelational())
        {
            await work(cancellationToken).ConfigureAwait(false);
            return true;
        }

        ArgumentNullException.ThrowIfNull(direct);

        long lockKey = DeriveLockKey(lockName);

        await using NpgsqlConnection gate = await direct.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await ScalarAsync(gate, "SELECT pg_try_advisory_lock(@key)", lockKey, null, cancellationToken)
                .ConfigureAwait(false) is not true)
        {
            return false;
        }

        bool released;

        try
        {
            await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            released = await TryReleaseAsync(gate, lockKey).ConfigureAwait(false);
        }

        if (!released)
        {
            logger?.LogError(
                "pg_advisory_unlock({LockKey}) for '{LockName}' released nothing, so the sweep lock has " +
                "leaked onto a server connection. Until it is recycled, no replica will run this sweep. " +
                "{SettingName} is not reaching PostgreSQL directly.",
                lockKey,
                lockName,
                PostgresDirect.SettingName);
        }

        return true;
    }

    internal static long DeriveLockKey(string lockName)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;

        foreach (char c in lockName)
        {
            hash ^= c;
            hash *= prime;
        }

        return unchecked((long)hash);
    }
}
