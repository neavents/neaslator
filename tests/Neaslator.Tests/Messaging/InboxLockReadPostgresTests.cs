using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neaslator.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Neaslator.Tests.Messaging;

/// <summary>
/// Two deliveries of one message interleaved the way MassTransit's inbox passes interleave them, on the
/// context as the service registers it. Driven by hand because the bus is composed inline in Program.cs,
/// so no test can host it with a probe consumer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InboxLockReadPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _provider = new ServiceCollection()
            .AddNeaslatorDbContext(_postgres.GetConnectionString())
            .BuildServiceProvider();

        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task The_inboxs_lock_read_returns_what_another_delivery_committed_not_the_row_this_one_tracked()
    {
        Guid messageId = NewId.NextGuid();
        Guid consumerId = NewId.NextGuid();

        await using AsyncServiceScope first = _provider.CreateAsyncScope();
        NeaslatorDbContext firstDb = first.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
        firstDb.Set<InboxState>().Add(new InboxState
        {
            MessageId = messageId,
            ConsumerId = consumerId,
            LockId = NewId.NextGuid(),
            Received = DateTime.UtcNow,
        });
        await firstDb.SaveChangesAsync();

        await using (AsyncServiceScope second = _provider.CreateAsyncScope())
        {
            NeaslatorDbContext secondDb = second.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            await using var transaction = await secondDb.Database.BeginTransactionAsync();
            InboxState consumedElsewhere = await LockAsync(secondDb, messageId, consumerId);
            consumedElsewhere.Consumed = DateTime.UtcNow;
            await secondDb.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var retry = await firstDb.Database.BeginTransactionAsync();
        InboxState locked = await LockAsync(firstDb, messageId, consumerId);

        locked.Consumed.Should().NotBeNull("the row lock must return the committed row, or this delivery consumes a message another already consumed");
    }

    private static Task<InboxState> LockAsync(DbContext db, Guid messageId, Guid consumerId)
    {
        string sql = new PostgresLockStatementProvider().GetRowLockStatement<InboxState>(db, nameof(InboxState.MessageId), nameof(InboxState.ConsumerId));
        return db.Set<InboxState>().FromSqlRaw(sql, messageId, consumerId).AsTracking().SingleAsync();
    }
}
