using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;
using Testcontainers.PostgreSql;

namespace Neaslator.Tests.Persistence;

/// <summary>
/// Only one replica may run a periodic sweep.
/// </summary>
/// <remarks>
/// <para>
/// The quality-upgrade job is a BackgroundService, so it runs on every replica. Each one selected
/// the same five hundred oldest degraded entries and re-translated all of them, which multiplied
/// both the provider bill and the provider QUOTA by the replica count. DeepSeek already exhausts
/// partway through a large run, so spending it several times over on identical work is the
/// difference between a sweep that finishes and one that never does.
/// </para>
/// <para>
/// Non-blocking on purpose. Startup work must wait — a replica that skipped migrating would serve
/// traffic against a half-migrated schema — but a sweep runs again on its own interval, and
/// queueing every replica behind the winner just means they all fire together when the lock frees.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AdvisoryLockPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private NeaslatorDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    [Fact]
    public async Task A_second_replica_does_not_run_the_work()
    {
        await using NeaslatorDbContext holder = CreateContext();
        await using NeaslatorDbContext rival = CreateContext();

        TaskCompletionSource holding = new();
        TaskCompletionSource release = new();

        Task<bool> first = holder.TryRunWithAdvisoryLockAsync("test:sweep", async _ =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        bool rivalRan = true;
        bool secondAcquired = await rival.TryRunWithAdvisoryLockAsync("test:sweep", _ =>
        {
            rivalRan = true;
            return Task.CompletedTask;
        });

        release.SetResult();
        (await first).Should().BeTrue("the first caller holds the lock and does the work");

        secondAcquired.Should().BeFalse(
            "a second replica must skip the sweep, not run it alongside — that is the duplicated provider bill");
        _ = rivalRan;
    }

    [Fact]
    public async Task The_work_does_not_run_when_the_lock_is_refused()
    {
        await using NeaslatorDbContext holder = CreateContext();
        await using NeaslatorDbContext rival = CreateContext();

        TaskCompletionSource holding = new();
        TaskCompletionSource release = new();

        Task<bool> first = holder.TryRunWithAdvisoryLockAsync("test:sweep-2", async _ =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        bool ran = false;
        await rival.TryRunWithAdvisoryLockAsync("test:sweep-2", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        release.SetResult();
        await first;

        ran.Should().BeFalse("skipping means not translating, or the guard bought nothing");
    }

    [Fact]
    public async Task The_lock_is_released_for_the_next_run()
    {
        await using NeaslatorDbContext first = CreateContext();
        await using NeaslatorDbContext second = CreateContext();

        (await first.TryRunWithAdvisoryLockAsync("test:sweep-3", _ => Task.CompletedTask))
            .Should().BeTrue();

        (await second.TryRunWithAdvisoryLockAsync("test:sweep-3", _ => Task.CompletedTask))
            .Should().BeTrue("otherwise one pass starves the sweep for the life of the deployment");
    }

    [Fact]
    public async Task A_throwing_sweep_still_releases_the_lock()
    {
        await using NeaslatorDbContext first = CreateContext();
        await using NeaslatorDbContext second = CreateContext();

        Func<Task> throwing = () => first.TryRunWithAdvisoryLockAsync(
            "test:sweep-4", _ => throw new InvalidOperationException("provider is down"));

        await throwing.Should().ThrowAsync<InvalidOperationException>();

        (await second.TryRunWithAdvisoryLockAsync("test:sweep-4", _ => Task.CompletedTask))
            .Should().BeTrue("a failed sweep must not lock every later one out");
    }

    [Fact]
    public async Task Different_names_do_not_block_each_other()
    {
        await using NeaslatorDbContext holder = CreateContext();
        await using NeaslatorDbContext other = CreateContext();

        TaskCompletionSource holding = new();
        TaskCompletionSource release = new();

        Task<bool> first = holder.TryRunWithAdvisoryLockAsync("test:sweep-a", async _ =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        (await other.TryRunWithAdvisoryLockAsync("test:sweep-b", _ => Task.CompletedTask))
            .Should().BeTrue("unrelated work must not wait behind this sweep");

        release.SetResult();
        await first;
    }
}
