using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslationMemoryStats;

public static class MemoryStatsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        // Read from a standby. These are aggregate counts over the whole translation memory —
        // four grouped scans, the most expensive read this service serves, and nobody is waiting
        // to watch a number move. Exactly the shape a replica exists for.
        group.MapGet(
            "/translate/v1/memory/stats",
            async (HttpRequest request, ReadReplica replica, CancellationToken ct) =>
            {
                await using var db = replica.Open();

                return await HandleAsync(GatewayCaller.From(request), db, ct);
            });
    }

    /// <summary>
    /// Every tenant's translation memory in one aggregate, so it is for operators and services only.
    /// </summary>
    internal static async Task<IResult> HandleAsync(GatewayCaller caller, NeaslatorDbContext db, CancellationToken ct)
    {
        if (!caller.SeesEveryTenant)
            return Forbidden();

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

    private static IResult Forbidden() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "FORBIDDEN",
        detail: "Translation memory statistics span every tenant and are for platform administrators.");
}
