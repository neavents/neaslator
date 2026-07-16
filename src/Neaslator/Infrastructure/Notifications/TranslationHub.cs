using Microsoft.AspNetCore.SignalR;

namespace Neaslator.Infrastructure.Notifications;

public sealed class TranslationHub : Hub
{
    public async Task JoinVenueGroup(Ulid ownerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"venue:{ownerId}");
    }

    public async Task JoinMenuGroup(Ulid menuId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"menu:{menuId}");
    }
}
