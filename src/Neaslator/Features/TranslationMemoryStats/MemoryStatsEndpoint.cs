using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationMemoryStats;

public static class MemoryStatsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/v1/translate/memory/stats", (NeaslatorDbContext db, CancellationToken ct) => HandleAsync(db, ct));
    }

    internal static async Task<IResult> HandleAsync(NeaslatorDbContext db, CancellationToken ct)
    {
        long totalEntries = await db.TranslationMemory.LongCountAsync(ct);
        long totalHits = await db.TranslationMemory.SumAsync(e => e.HitCount, ct);

        var entriesByProviderTier = await db.TranslationMemory
            .GroupBy(e => e.ProviderTier)
            .Select(g => new { tier = g.Key.ToString(), count = g.LongCount() })
            .OrderBy(x => x.tier)
            .ToListAsync(ct);

        var entriesBySourceLanguage = await db.TranslationMemory
            .GroupBy(e => e.SourceLanguageCode)
            .Select(g => new { language = g.Key, count = g.LongCount() })
            .OrderByDescending(x => x.count)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            totalEntries,
            totalHits,
            entriesByProviderTier,
            entriesBySourceLanguage
        });
    }
}
