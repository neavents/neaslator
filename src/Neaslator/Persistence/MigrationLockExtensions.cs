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
