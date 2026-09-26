using System.Data.Common;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Neaslator.Infrastructure.Messaging;

/// <summary>
/// Makes the inbox's row lock read what it locked. MassTransit 8.5.5 runs its inbox as several
/// passes on one DbContext, each opening with <c>SELECT … FROM "InboxState" … FOR UPDATE</c> and
/// tracking the result. The row inserted in the first pass is still tracked when the second locks it,
/// and EF's identity resolution then hands back that tracked copy and discards the row the database
/// returned. When a second delivery of the same message took the lock in between and consumed it,
/// the first pass-two sees the stale "not consumed", writes every column back over it and runs the
/// consumer again. Detaching the already-saved InboxState before each lock read makes the database's
/// answer the one the inbox acts on.
/// </summary>
public sealed class InboxLockReadInterceptor : DbCommandInterceptor
{
    public static bool IsInboxLock(DbCommand command) =>
        command is not null
        && command.CommandText.Contains("\"InboxState\"", StringComparison.Ordinal)
        && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Forget(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Forget(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void Forget(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is not { } context || !IsInboxLock(command)) return;
        foreach (var entry in context.ChangeTracker.Entries<InboxState>().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }
}
