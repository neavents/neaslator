using FluentAssertions;
using Neaslator.Infrastructure.Messaging;
using Npgsql;
using Xunit;

namespace Neaslator.Tests.Messaging;

public sealed class InboxLockReadInterceptorTests
{
    [Theory]
    [InlineData("SELECT *, xmin FROM \"neaslator\".\"InboxState\" WHERE \"MessageId\" = @p0 AND \"ConsumerId\" = @p1 FOR UPDATE", true)]
    [InlineData("SELECT * FROM \"neaslator\".\"InboxState\" WHERE \"Delivered\" < @p0", false)]
    [InlineData("SELECT * FROM \"neaslator\".\"OutboxState\" FOR UPDATE", false)]
    public void Only_the_inboxs_row_lock_read_is_intercepted(string sql, bool intercepted)
    {
        using NpgsqlCommand command = new(sql);

        InboxLockReadInterceptor.IsInboxLock(command).Should().Be(intercepted);
    }
}
