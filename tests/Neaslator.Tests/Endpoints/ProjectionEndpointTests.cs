using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslationMemoryStats;
using Neaslator.Features.TranslationStatus;
using Neaslator.Persistence;

namespace Neaslator.Tests.Endpoints;

/// <summary>
/// Handler coverage for the read-only projection endpoints (language list, translation-memory
/// stats). Runs against an InMemory context and inspects the serialized JSON body.
/// </summary>
public sealed class ProjectionEndpointTests : IDisposable
{
    private readonly NeaslatorDbContext _db;
    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

    public ProjectionEndpointTests()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new NeaslatorDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private static async Task<(int status, JsonDocument body)> Execute(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        string text = await new StreamReader(stream).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(text));
    }

    private static JsonElement Prop(JsonElement e, string name)
    {
        foreach (JsonProperty p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        throw new KeyNotFoundException(name);
    }

    // ───── Languages ─────

    [Fact]
    public async Task Languages_OnlyActive_OrderedBySortOrder()
    {
        _db.SupportedLanguages.AddRange(
            new SupportedLanguage { Code = "fr", EnglishName = "French", NativeName = "Francais", IsActive = true, SortOrder = 2 },
            new SupportedLanguage { Code = "de", EnglishName = "German", NativeName = "Deutsch", IsActive = true, SortOrder = 1 },
            new SupportedLanguage { Code = "en", EnglishName = "English", NativeName = "English", IsActive = true, SortOrder = 0 },
            new SupportedLanguage { Code = "es", EnglishName = "Spanish", NativeName = "Espanol", IsActive = false, SortOrder = 3 });
        await _db.SaveChangesAsync();

        (int status, JsonDocument body) = await Execute(await ListLanguagesEndpoint.HandleAsync(_db, CancellationToken.None));
        using JsonDocument _ = body;

        status.Should().Be(StatusCodes.Status200OK);
        JsonElement arr = body.RootElement;
        arr.GetArrayLength().Should().Be(3, "the inactive language is excluded");

        List<string> codes = arr.EnumerateArray().Select(e => Prop(e, "code").GetString()!).ToList();
        codes.Should().ContainInOrder("en", "de", "fr");
        codes.Should().NotContain("es");
    }

    [Fact]
    public async Task Languages_None_ReturnsEmptyArray()
    {
        (int status, JsonDocument body) = await Execute(await ListLanguagesEndpoint.HandleAsync(_db, CancellationToken.None));
        using JsonDocument _ = body;

        status.Should().Be(StatusCodes.Status200OK);
        body.RootElement.GetArrayLength().Should().Be(0);
    }

    // ───── Memory stats ─────

    private async Task SeedEntry(long hash, string sourceLang, TranslationProviderTier tier, long hitCount)
    {
        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = hash,
            NormalizedSourceText = $"t{hash}",
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "tr",
            TranslatedText = "x",
            ProviderTier = tier,
            ProviderName = "deepseek",
            ConfidenceScore = 1f,
            HitCount = hitCount,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task MemoryStats_AggregatesTotalsTiersAndLanguages()
    {
        await SeedEntry(1, "en", TranslationProviderTier.Primary, 1);
        await SeedEntry(2, "en", TranslationProviderTier.Primary, 2);
        await SeedEntry(3, "es", TranslationProviderTier.Secondary, 3);

        (int status, JsonDocument body) = await Execute(await MemoryStatsEndpoint.HandleAsync(_db, CancellationToken.None));
        using JsonDocument _ = body;

        status.Should().Be(StatusCodes.Status200OK);
        JsonElement root = body.RootElement;
        Prop(root, "totalEntries").GetInt64().Should().Be(3);
        Prop(root, "totalHits").GetInt64().Should().Be(6);

        JsonElement tiers = Prop(root, "entriesByProviderTier");
        long primaryCount = tiers.EnumerateArray()
            .First(e => Prop(e, "tier").GetString() == "Primary")
            .GetProperty("count").GetInt64();
        primaryCount.Should().Be(2);

        JsonElement langs = Prop(root, "entriesBySourceLanguage");
        long enCount = langs.EnumerateArray()
            .First(e => Prop(e, "language").GetString() == "en")
            .GetProperty("count").GetInt64();
        enCount.Should().Be(2);
    }

    [Fact]
    public async Task MemoryStats_Empty_ReturnsZeroTotals()
    {
        (int status, JsonDocument body) = await Execute(await MemoryStatsEndpoint.HandleAsync(_db, CancellationToken.None));
        using JsonDocument _ = body;

        status.Should().Be(StatusCodes.Status200OK);
        Prop(body.RootElement, "totalEntries").GetInt64().Should().Be(0);
        Prop(body.RootElement, "totalHits").GetInt64().Should().Be(0);
    }
}
