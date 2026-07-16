using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationStatus;

public static class ListLanguagesEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/v1/languages", async (NeaslatorDbContext db, CancellationToken ct) =>
        {
            var languages = await db.SupportedLanguages
                .Where(l => l.IsActive)
                .OrderBy(l => l.SortOrder)
                .Select(l => new { l.Code, l.EnglishName, l.NativeName, l.SortOrder })
                .AsNoTracking()
                .ToListAsync(ct);

            return Results.Ok(languages);
        });
    }
}
