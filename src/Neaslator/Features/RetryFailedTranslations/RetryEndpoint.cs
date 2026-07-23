using MassTransit;
using Microsoft.EntityFrameworkCore;
using Neaslator.Features.TranslateMenu;
using Neaslator.Persistence;

namespace Neaslator.Features.RetryFailedTranslations;

public static class RetryEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/api/v1/translate/menu/{menuId}/retry", (
            string menuId,
            RetryTranslationRequest request,
            NeaslatorDbContext db,
            IPublishEndpoint publisher,
            CancellationToken ct) => HandleAsync(menuId, request, db, publisher, ct));
    }

    internal static async Task<IResult> HandleAsync(
        string menuId,
        RetryTranslationRequest request,
        NeaslatorDbContext db,
        IPublishEndpoint publisher,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(menuId, out Ulid parsedMenuId))
            return Results.BadRequest(new { error = "Invalid menu ID format" });

        if (string.IsNullOrWhiteSpace(request.SourceLanguageCode))
            return Results.BadRequest(new { error = "SourceLanguageCode is required" });

        if (string.IsNullOrWhiteSpace(request.VenueType))
            return Results.BadRequest(new { error = "VenueType is required" });

        if (string.IsNullOrWhiteSpace(request.CuisineType))
            return Results.BadRequest(new { error = "CuisineType is required" });

        var snapshot = await db.MenuPublishSnapshots
            .Where(s => s.MenuId == parsedMenuId)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
            return Results.NotFound(new { error = "No translation history for this menu" });

        await publisher.Publish(new StartTranslationCommand
        {
            MenuId = parsedMenuId,
            OwnerId = snapshot.OwnerId,
            SourceLanguageCode = request.SourceLanguageCode,
            VenueType = request.VenueType,
            CuisineType = request.CuisineType,
            TriggeredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Accepted(value: new { menuId = parsedMenuId, status = "retry_scheduled" });
    }
}

public sealed record RetryTranslationRequest(
    string SourceLanguageCode,
    string VenueType,
    string CuisineType);
