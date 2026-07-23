using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neaslator.Features.QualityUpgrade;
using NSubstitute;

namespace Neaslator.Tests.QualityUpgrade;

/// <summary>
/// The BackgroundService wrapper around the upgrade work. The job waits a full interval
/// before doing anything, so a host shutdown during that wait must exit cleanly and never
/// touch the database (no scope is created until the delay elapses).
/// </summary>
public sealed class QualityUpgradeJobLoopTests
{
    [Fact]
    public async Task CancelledBeforeInterval_ExitsCleanly_NoWorkPerformed()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        var job = new QualityUpgradeJob(scopeFactory, Substitute.For<ILogger<QualityUpgradeJob>>());

        // StartAsync begins ExecuteAsync, which immediately awaits the 6h interval delay.
        await job.StartAsync(CancellationToken.None);
        // StopAsync signals the stopping token, cancelling the delay -> loop breaks cleanly.
        Func<Task> stop = () => job.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();

        // The interval never elapsed, so no scope/db work was attempted.
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task StopWithoutStart_DoesNotThrow()
    {
        var job = new QualityUpgradeJob(Substitute.For<IServiceScopeFactory>(), Substitute.For<ILogger<QualityUpgradeJob>>());

        Func<Task> stop = () => job.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
    }
}
