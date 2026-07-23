using Microsoft.AspNetCore.SignalR;
using Neaslator.Infrastructure.Notifications;
using NSubstitute;

namespace Neaslator.Tests.Notifications;

/// <summary>
/// The SignalR hub clients call to subscribe to progress updates. Join methods must add the
/// caller's connection to the venue/menu group whose name matches what
/// <see cref="TranslationNotifier"/> publishes to (venue:{id} / menu:{id}).
/// </summary>
public sealed class TranslationHubTests
{
    private readonly IGroupManager _groups = Substitute.For<IGroupManager>();

    private TranslationHub CreateHub(string connectionId = "conn-1")
    {
        HubCallerContext context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        return new TranslationHub { Context = context, Groups = _groups };
    }

    [Fact]
    public async Task JoinVenueGroup_AddsConnectionToVenueGroup()
    {
        Ulid ownerId = Ulid.NewUlid();
        TranslationHub hub = CreateHub("conn-abc");

        await hub.JoinVenueGroup(ownerId);

        await _groups.Received(1).AddToGroupAsync("conn-abc", $"venue:{ownerId}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinMenuGroup_AddsConnectionToMenuGroup()
    {
        Ulid menuId = Ulid.NewUlid();
        TranslationHub hub = CreateHub("conn-xyz");

        await hub.JoinMenuGroup(menuId);

        await _groups.Received(1).AddToGroupAsync("conn-xyz", $"menu:{menuId}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinVenueGroup_GroupNameMatchesNotifierPrefix()
    {
        // TranslationNotifier.NotifyOwnerAsync publishes to "venue:{ownerId}"; the hub must
        // subscribe to the identical name or clients never receive updates.
        Ulid ownerId = Ulid.NewUlid();
        TranslationHub hub = CreateHub();

        await hub.JoinVenueGroup(ownerId);

        await _groups.Received().AddToGroupAsync(
            Arg.Any<string>(),
            Arg.Is<string>(g => g == $"venue:{ownerId}"),
            Arg.Any<CancellationToken>());
    }
}
