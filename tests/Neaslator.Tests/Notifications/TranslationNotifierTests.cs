using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Notifications;
using Neaslator.Tests.Shared;
using NSubstitute;

namespace Neaslator.Tests.Notifications;

public sealed class TranslationNotifierTests : UnitTestBase
{
    private readonly IHubContext<TranslationHub> _hubContext;
    private readonly IClientProxy _clientProxy;
    private readonly TranslationNotifier _sut;

    public TranslationNotifierTests()
    {
        _clientProxy = Substitute.For<IClientProxy>();
        _hubContext = Substitute.For<IHubContext<TranslationHub>>();
        _hubContext.Clients.Returns(Substitute.For<IHubClients>());
        _hubContext.Clients.Group(Arg.Any<string>()).Returns(_clientProxy);

        _sut = new TranslationNotifier(_hubContext);
    }

    [Fact]
    public async Task NotifyOwnerAsync_SendsToCorrectGroup()
    {
        var ownerId = Ulid.NewUlid();
        var notification = new TranslationStatusNotification(
            Ulid.NewUlid(), TranslationNotificationType.Started, 5, 0, 0);

        await _sut.NotifyOwnerAsync(ownerId, notification);

        await _clientProxy.ReceivedWithAnyArgs(1).SendCoreAsync(default!, default!, default);
    }

    [Fact]
    public async Task NotifyMenuAsync_SendsToCorrectGroup()
    {
        var menuId = Ulid.NewUlid();
        var notification = new TranslationStatusNotification(
            menuId, TranslationNotificationType.Completed, 3, 3, 0);

        await _sut.NotifyMenuAsync(menuId, notification);

        await _clientProxy.ReceivedWithAnyArgs(1).SendCoreAsync(default!, default!, default);
    }

    [Fact]
    public async Task NotifyOwnerAsync_WithFailedLanguages_UsesGroupPrefix()
    {
        var ownerId = Ulid.NewUlid();
        var notification = new TranslationStatusNotification(
            Ulid.NewUlid(), TranslationNotificationType.Failed, 5, 2, 3, "Provider timeout");

        await _sut.NotifyOwnerAsync(ownerId, notification);

        _hubContext.Clients.Received(1).Group($"venue:{ownerId}");
    }

    [Fact]
    public async Task NotifyMenuAsync_UsesMenuGroupPrefix()
    {
        var menuId = Ulid.NewUlid();
        var notification = new TranslationStatusNotification(
            menuId, TranslationNotificationType.Progress, 10, 5, 0);

        await _sut.NotifyMenuAsync(menuId, notification);

        _hubContext.Clients.Received(1).Group($"menu:{menuId}");
    }

    [Fact]
    public void TranslationStatusNotification_AllProperties_Set()
    {
        var menuId = Ulid.NewUlid();
        var notification = new TranslationStatusNotification(
            menuId, TranslationNotificationType.Progress, 10, 5, 1, "One failed");

        notification.MenuId.Should().Be(menuId);
        notification.Type.Should().Be(TranslationNotificationType.Progress);
        notification.TotalLanguages.Should().Be(10);
        notification.CompletedLanguages.Should().Be(5);
        notification.FailedLanguages.Should().Be(1);
        notification.ErrorSummary.Should().Be("One failed");
    }
}
