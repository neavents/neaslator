using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Neaslator.Persistence;

/// <summary>
/// Serializes startup migrations across every process sharing a database.
///
/// Every service in this estate migrates on boot against the same Postgres instance with no
/// coordination, and every replica of a service does it at the same moment a rollout starts them.
/// <c>MigrateAsync</c> reads <c>__EFMigrationsHistory</c>, computes the pending list, then applies
/// each migration in its own transaction — so two processes that read that table before either has
/// written to it both compute the <em>same</em> pending list and then interleave the DDL. That has
/// already caused four separate incidents here:
/// <list type="bullet">
/// <item>dracula crash-looped on <c>42P07 relation "InboxState" already exists</c>;</item>
/// <item>media logged <c>42P07 classification_managements already exists</c> and carried on,
///       silently tolerating schema drift for weeks;</item>
/// <item>menu died on <c>42701 column "created_at_utc" already exists</c> after a migration
///       had been left ~70% applied;</item>
/// <item>identity threw <c>42704 constraint "FK_organization_users_accounts_account_id" ... does
///       not exist</c>, one process having already replaced a foreign key the other was still
///       trying to drop.</item>
/// </list>
/// A session-level advisory lock makes concurrent starts queue instead of racing. It is
/// released automatically if the process dies, so a crashed migrator cannot wedge the estate.
///
/// <para>
/// <b>The lock is taken on <see cref="PostgresDirect"/>, not on the DbContext's connection.</b> A
/// session lock belongs to a server connection, so it needs one that stays put; the migrations
/// themselves do not, since EF applies each in its own transaction and a transaction is the unit a
/// pooler is safe with. Splitting them that way is what lets the ordinary connection string sit
/// behind PgBouncer later without the lock quietly ceasing to work. <c>PostgresDirect</c> proves at
/// startup that it can actually hold the lock and refuses to boot if it cannot.
/// </para>
///
/// <para>
/// <b>Why neaslator only got this today.</b> It had no migration step at all. Three migrations
/// existed in source and exactly ONE — InitialCreate, from 14 June — had ever been applied. The
/// schema froze there, which meant AddMassTransitInboxOutbox never ran, which meant OutboxState did
/// not exist, which meant every publish staged through UseBusOutbox() failed at SaveChangesAsync.
/// Neaslator could not emit a single integration event. That is why translations never reached KV
/// and why menu.smart_menu_translations held one row. The missing table was the symptom; a service
/// that never migrates was the cause.
/// </para>
///
/// <para>
/// This file is a copy. The estate keeps no shared infrastructure package, so the same helper lives
/// in menu, media, messaging, dracula and subscription, and identity has a handle-based variant that
/// also spans its startup seeding. A fix here reaches none of the others — change them together.
/// </para>
/// </summary>
public static class MigrationLockExtensions
{
    /// <summary>
    /// How long to wait for another process to finish migrating before failing.
    /// </summary>
    /// <remarks>
    /// <c>pg_advisory_lock</c> waits forever by default, which turns a wedged migrator into a hung
    /// boot that no orchestrator can tell apart from a slow one. Npgsql's command timeout cancels
    /// the wait and throws instead, so the process dies with a message and gets restarted. Four
    /// minutes sits just inside the Kubernetes startup probe's five, so a stuck migration reports
    /// what happened rather than being killed mid-wait with nothing in the log.
    /// </remarks>
    private const int LockWaitTimeoutSeconds = 240;

    /// <summary>
    /// Applies pending migrations while holding a Postgres advisory lock derived from
    /// <paramref name="lockName"/> (use the owning schema, so unrelated services do not
    /// block each other).
    /// </summary>
    /// <param name="direct">
    /// The connection the lock is taken on. Validated here, before the lock is attempted, so a
    /// connection that cannot hold a session lock stops the service instead of letting every
    /// replica migrate at once.
    /// </param>
    /// <param name="afterMigration">
    /// Startup work that must not race either — seeders are check-then-insert, so two replicas both
    /// read "absent" and both insert. Runs inside the lock, on the DbContext's own connection.
    /// </param>
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

        // One line, once per boot. The fallback is the state worth seeing in a log: it is correct
        // today and becomes wrong the moment a pooler appears, and this is what says which of the
        // two a running process is in.
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
            // Not passing the caller's token: if it was cancelled mid-migration this is exactly
            // when the lock most needs releasing, and a cancelled unlock would leave every other
            // replica waiting on a lock nobody is using. Never throws, so a failure here cannot
            // replace a real migration error with a misleading one.
            released = await TryReleaseAsync(gate, lockKey).ConfigureAwait(false);
        }

        if (!released)
        {
            // pg_advisory_unlock returns false when this session did not hold the lock. On a
            // connection that stayed put, that cannot happen — so it means the connection did not,
            // and the lock is now stranded on whatever server connection took it. Every other
            // replica will queue behind it until that connection is recycled.
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
            // Disposing the connection ends the session, which releases the lock regardless.
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

        // ExecuteScalar, because for pg_advisory_unlock the answer IS the returned value.
        // ExecuteNonQuery reports rows affected — minus one for a SELECT — which is the read that
        // made ten "singleton" jobs in this estate run on every replica for months.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> only if no other process holds <paramref name="lockName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The non-blocking counterpart of <see cref="MigrateWithAdvisoryLockAsync"/>, and the wait is
    /// the whole difference. Startup work must BLOCK: a replica that skipped migrating would serve
    /// traffic against a half-migrated schema. A periodic sweep must SKIP: it runs again on its own
    /// interval, and queueing every replica behind the winner just means they all fire the moment
    /// the lock frees.
    /// </para>
    /// <para>
    /// The lock is held on a <see cref="PostgresDirect"/> connection for the duration of the work,
    /// which is the only place it can live: a session advisory lock dies with its session, and EF
    /// hands its connection back to the pool between commands — so a lock taken that way ends up
    /// attached to a session given to whoever asks next, while the unlock runs on whatever
    /// connection EF supplies then and quietly returns false.
    /// </para>
    /// <para>
    /// Read with ExecuteScalar. <c>ExecuteSqlRawAsync</c> would report rows affected — minus one
    /// for a SELECT — which is never zero, so a guard written that way never fires at all.
    /// </para>
    /// </remarks>
    /// <param name="direct">
    /// The connection to take the lock on. May be null <em>only</em> when
    /// <paramref name="context"/> is not relational; a relational context with no direct source is
    /// a wiring mistake that would run the sweep on every replica, so it throws rather than
    /// silently degrading.
    /// </param>
    /// <returns>True when this process ran the work; false when another already holds the lock.</returns>
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

        // An advisory lock is a Postgres construct. A non-relational context has no connection to
        // take one on — and, being an in-memory store private to one process, has nothing to
        // contend with either. Running the work unguarded there is correct rather than a
        // concession: the guard exists to coordinate processes that share a database, and such a
        // context by definition does not.
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
            // Not passing the caller's token: a cancelled sweep is exactly when the lock most needs
            // releasing, and a cancelled unlock would keep every other replica out until the
            // connection is recycled.
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

    /// <summary>
    /// Stable 64-bit key from the lock name. Advisory locks are a flat bigint namespace, so
    /// the mapping only has to be deterministic across processes and unlikely to collide —
    /// string.GetHashCode is randomized per process and would not do, and a lock whose key differs
    /// per process is the worst possible failure because it looks fixed while contending with nothing.
    /// </summary>
    internal static long DeriveLockKey(string lockName)
    {
        // FNV-1a 64-bit.
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
