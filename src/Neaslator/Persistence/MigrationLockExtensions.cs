using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
/// This file is a copy. The estate keeps no shared infrastructure package, so the same helper lives
/// in menu, media, messaging, dracula and subscription, and identity has a handle-based variant that
/// also spans its startup seeding. A fix here reaches none of the others — change them together.
/// </para>
/// <para>
/// <b>Why neaslator only got this today.</b> It had no migration step at all. Three migrations
/// existed in source and exactly ONE — InitialCreate, from 14 June — had ever been applied. The
/// schema froze there, which meant AddMassTransitInboxOutbox never ran, which meant OutboxState did
/// not exist, which meant every publish staged through UseBusOutbox() failed at SaveChangesAsync.
/// Neaslator could not emit a single integration event. That is why translations never reached KV
/// and why menu.smart_menu_translations held one row. The missing table was the symptom; a service
/// that never migrates was the cause.
/// </para>
/// </summary>
public static class MigrationLockExtensions
{
    /// <summary>
    /// Applies pending migrations while holding a Postgres advisory lock derived from
    /// <paramref name="lockName"/> (use the owning schema, so unrelated services do not
    /// block each other).
    /// </summary>
    /// <param name="afterMigration">
    /// Startup work that must not race either — seeders are check-then-insert, so two replicas both
    /// read "absent" and both insert. Runs inside the lock, on the same open connection.
    /// </param>
    public static async Task MigrateWithAdvisoryLockAsync(
        this DbContext context,
        string lockName,
        Func<CancellationToken, Task>? afterMigration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName, nameof(lockName));

        long lockKey = DeriveLockKey(lockName);

        // The lock must be taken on the *same* connection that runs the migrations, so open
        // it explicitly rather than letting EF open and close one per command.
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(context, "SELECT pg_advisory_lock(@key)", lockKey, cancellationToken)
                .ConfigureAwait(false);

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
                // replica waiting on a lock nobody is using.
                await ExecuteAsync(context, "SELECT pg_advisory_unlock(@key)", lockKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(DbContext context, string sql, long lockKey, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;
        command.Parameters.AddWithValue("key", lockKey);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
    /// The connection is opened explicitly and held for the duration. A session advisory lock lives
    /// and dies with its session, and EF hands its connection back to the pool between commands —
    /// so a lock taken on a pooled connection ends up attached to a session given to whoever asks
    /// next, while the unlock runs on whatever connection EF supplies then and quietly returns
    /// false.
    /// </para>
    /// <para>
    /// Read with ExecuteScalar. <c>ExecuteSqlRawAsync</c> would report rows affected — minus one
    /// for a SELECT — which is never zero, so a guard written that way never fires at all.
    /// </para>
    /// </remarks>
    /// <returns>True when this process ran the work; false when another already holds the lock.</returns>
    public static async Task<bool> TryRunWithAdvisoryLockAsync(
        this DbContext context,
        string lockName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

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

        long lockKey = DeriveLockKey(lockName);

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlCommand acquire =
                (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();

            acquire.CommandText = "SELECT pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", lockKey);

            if (await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }

            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Not passing the caller's token: a cancelled sweep is exactly when the lock most
                // needs releasing, and a cancelled unlock would keep every other replica out until
                // the connection is recycled.
                await ExecuteAsync(context, "SELECT pg_advisory_unlock(@key)", lockKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stable 64-bit key from the lock name. Advisory locks are a flat bigint namespace, so
    /// the mapping only has to be deterministic across processes and unlikely to collide —
    /// string.GetHashCode is randomized per process and would not do, and a lock whose key differs
    /// per process is the worst possible failure because it looks fixed while contending with nothing.
    /// </summary>
    private static long DeriveLockKey(string lockName)
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
