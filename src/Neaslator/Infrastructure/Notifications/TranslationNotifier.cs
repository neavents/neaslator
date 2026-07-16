using Microsoft.AspNetCore.SignalR;
using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Notifications;

public sealed class TranslationNotifier
{
    private readonly IHubContext<TranslationHub> _hub;

    public TranslationNotifier(IHubContext<TranslationHub> hubContext)
    {
        _hub = hubContext;
    }

    public async Task NotifyOwnerAsync(Ulid ownerId, TranslationStatusNotification notification)
    {
        await _hub.Clients
            .Group($"venue:{ownerId}")
            .SendAsync("TranslationStatus", notification);
    }

    public async Task NotifyMenuAsync(Ulid menuId, TranslationStatusNotification notification)
    {
        await _hub.Clients
            .Group($"menu:{menuId}")
            .SendAsync("TranslationStatus", notification);
    }
}

public sealed record TranslationStatusNotification(
    Ulid MenuId,
    TranslationNotificationType Type,
    int TotalLanguages,
    int CompletedLanguages,
    int FailedLanguages,
    string? ErrorSummary = null);
