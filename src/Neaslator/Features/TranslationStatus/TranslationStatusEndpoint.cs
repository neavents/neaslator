using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationStatus;

public static class TranslationStatusEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/v1/translate/menu/{menuId}/status", async (string menuId, NeaslatorDbContext db, CancellationToken ct) =>
        {
            if (!Ulid.TryParse(menuId, out Ulid parsedMenuId))
                return Results.BadRequest(new { error = "Invalid menu ID format" });

            var snapshot = await db.MenuPublishSnapshots
                .Where(s => s.MenuId == parsedMenuId)
                .Select(s => new
                {
                    s.MenuId,
                    s.OwnerId,
                    s.PublishedAt,
                    HasSnapshot = true
                })
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (snapshot is null)
                return Results.NotFound(new { error = "No translation history for this menu" });

            return Results.Ok(snapshot);
        });
    }
}
