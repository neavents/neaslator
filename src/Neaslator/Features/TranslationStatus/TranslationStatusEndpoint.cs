using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationStatus;

public static class TranslationStatusEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/translate/v1/menu/{menuId}/status",
            (string menuId, HttpRequest request, NeaslatorDbContext db, CancellationToken ct) =>
                HandleAsync(menuId, GatewayCaller.From(request), db, ct));
    }

    internal static async Task<IResult> HandleAsync(
        string menuId, GatewayCaller caller, NeaslatorDbContext db, CancellationToken ct)
    {
        if (!Ulid.TryParse(menuId, out Ulid parsedMenuId))
            return Results.BadRequest(new { error = "Invalid menu ID format" });

        var snapshot = await db.MenuPublishSnapshots
            .Where(s => s.MenuId == parsedMenuId)
            .Select(s => new
            {
                s.MenuId,
                s.OwnerId,
                s.TenantId,
                s.PublishedAt
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        // Another tenant's menu answers exactly like a menu with no history, so the id space does not leak.
        if (snapshot is null || !caller.MaySee(snapshot.TenantId))
            return Results.NotFound(new { error = "No translation history for this menu" });

        return Results.Ok(new { snapshot.MenuId, snapshot.OwnerId, snapshot.PublishedAt, HasSnapshot = true });
    }
}
