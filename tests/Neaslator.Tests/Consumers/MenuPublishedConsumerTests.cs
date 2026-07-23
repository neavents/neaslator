using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Features.TranslateMenu;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Consumers;

/// <summary>
/// Debounce behaviour of <see cref="MenuPublishedConsumer"/> via the MassTransit in-memory
/// test harness. The Garnet SETNX result drives the two paths: the first publish in a window
/// schedules a StartTranslationCommand, while a duplicate coalesces and only extends the TTL.
/// The scheduled command carries a 5s delay and is never delivered within the test (the harness
/// is stopped first) — we assert the consumer's Redis behaviour, which is the actual logic.
/// </summary>
public sealed class MenuPublishedConsumerTests
{
    private static ServiceProvider BuildHarness(IConnectionMultiplexer garnet)
    {
        return new ServiceCollection()
            .AddSingleton(garnet)
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddConsumer<MenuPublishedConsumer>();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);
    }

    private static IConnectionMultiplexer Garnet(bool setNxResult, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.NotExists)
            .Returns(setNxResult);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>())
            .Returns(true);
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return mux;
    }

    private static MenuPublishedEvent Event(Ulid menuId) => new()
    {
        OwnerId = Ulid.NewUlid(),
        MenuId = menuId,
        PublishedAt = DateTimeOffset.UtcNow,
        SourceLanguageCode = "en",
        VenueType = "Restaurant",
        CuisineType = "Italian"
    };

    [Fact]
    public async Task FirstPublishInWindow_Consumed_DoesNotCoalesce()
    {
        IConnectionMultiplexer garnet = Garnet(setNxResult: true, out IDatabase redis);
        await using ServiceProvider provider = BuildHarness(garnet);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event(Ulid.NewUlid()));

            (await harness.Consumed.Any<MenuPublishedEvent>()).Should().BeTrue();
            IConsumerTestHarness<MenuPublishedConsumer> consumer = harness.GetConsumerHarness<MenuPublishedConsumer>();
            (await consumer.Consumed.Any<MenuPublishedEvent>()).Should().BeTrue();

            // First-in-window path must NOT extend an existing TTL.
            await redis.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DuplicatePublishInWindow_Coalesces_ExtendsTtl()
    {
        IConnectionMultiplexer garnet = Garnet(setNxResult: false, out IDatabase redis);
        await using ServiceProvider provider = BuildHarness(garnet);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event(Ulid.NewUlid()));

            (await harness.Consumed.Any<MenuPublishedEvent>()).Should().BeTrue();

            // Coalesced path extends the debounce window.
            await redis.Received(1).KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DebounceKey_IsScopedToMenuId()
    {
        Ulid menuId = Ulid.NewUlid();
        IConnectionMultiplexer garnet = Garnet(setNxResult: true, out IDatabase redis);
        await using ServiceProvider provider = BuildHarness(garnet);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event(menuId));

            (await harness.Consumed.Any<MenuPublishedEvent>()).Should().BeTrue();

            await redis.Received().StringSetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == $"neaslator:debounce:{menuId}"),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                When.NotExists);
        }
        finally
        {
            await harness.Stop();
        }
    }
}
