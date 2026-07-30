using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;
using Xunit;

namespace Neaslator.Tests.Messaging;

/// <summary>
/// The inbox and outbox tables must be mapped, or every consumer in this service goes offline.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddEntityFrameworkOutbox&lt;NeaslatorDbContext&gt;</c> makes MassTransit resolve
/// <c>InboxState</c>, <c>OutboxState</c> and <c>OutboxMessage</c> as DbSets on this context. If
/// <c>OnModelCreating</c> does not map them, the filter fails to resolve them and the RECEIVE ENDPOINT
/// FAILS TO START — not the one message, the endpoint. So a missing mapping does not degrade
/// consumption, it removes it, and identity's context carries the same warning for the same reason.
/// </para>
/// <para>
/// <b>Why this is a model test rather than a live-bus probe.</b> identity and subscription both assert
/// their inbox against the RUNNING bus via <c>GetProbeResult()</c>, which is the stronger check — it
/// proves the filter reached the endpoint rather than that the call was written. neaslator's suite has
/// no host harness to hang that on, so this asserts the precondition instead: the mapping the
/// registration depends on. Weaker, and worth saying so rather than implying equivalence.
/// </para>
/// </remarks>
public sealed class InboxOutboxMappingTests
{
    [Theory]
    [InlineData(typeof(InboxState))]
    [InlineData(typeof(OutboxState))]
    [InlineData(typeof(OutboxMessage))]
    public void The_transactional_entity_is_mapped(Type entity)
    {
        // A real model, built the way the app builds it. Asserting against the registration call in
        // Program.cs would only prove someone wrote the line; this proves EF agrees.
        var options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql("Host=model-only.invalid;Database=none;Username=none;Password=none")
            .Options;

        using var context = new NeaslatorDbContext(options);

        context.Model.FindEntityType(entity).Should().NotBeNull(
            $"{entity.Name} is not mapped, so AddEntityFrameworkOutbox cannot resolve it and every "
            + "receive endpoint in this service fails to start. That is not degraded consumption — it "
            + "is no consumption, and the service still reports healthy");
    }

    [Fact]
    public void All_three_are_mapped_together()
    {
        // They are a set. Mapping the inbox without the outbox (or the reverse) is the shape that
        // slips through review, because each line looks correct on its own and the failure only
        // appears when the filter tries to resolve the one that is missing.
        var options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql("Host=model-only.invalid;Database=none;Username=none;Password=none")
            .Options;

        using var context = new NeaslatorDbContext(options);

        new[] { typeof(InboxState), typeof(OutboxState), typeof(OutboxMessage) }
            .Where(t => context.Model.FindEntityType(t) is null)
            .Should().BeEmpty("the inbox and outbox are mapped as a set or not at all");
    }
}
