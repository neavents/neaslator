using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationStatus;

public static class ListLanguagesEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        // Read from a standby. The supported-language list is read on essentially every
        // translation request and changes when somebody adds a language, so a second of staleness
        // is not a contract anyone depends on. Nothing writes it in the request path, which is the
        // actual test — see ReadReplica.
        group.MapGet(
            "/translate/v1/languages",
            async (ReadReplica replica, CancellationToken ct) =>
            {
                await using var db = replica.Open();

                return await HandleAsync(db, ct);
            });
    }

    internal static async Task<IResult> HandleAsync(NeaslatorDbContext db, CancellationToken ct)
    {
        var languages = await db.SupportedLanguages
            .Where(l => l.IsActive)
            .OrderBy(l => l.SortOrder)
            .Select(l => new { l.Code, l.EnglishName, l.NativeName, l.SortOrder })
            .AsNoTracking()
            .ToListAsync(ct);

        return Results.Ok(languages);
    }
}
