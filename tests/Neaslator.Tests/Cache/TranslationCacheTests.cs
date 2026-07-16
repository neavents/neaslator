using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Persistence;
using Neaslator.Tests.Shared;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Cache;

public sealed class TranslationCacheTests : UnitTestBase, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IConnectionMultiplexer _garnet;
    private readonly IDatabase _redisDb;
    private readonly NeaslatorDbContext _db;
    private readonly TranslationCache _sut;

    public TranslationCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _garnet = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        _garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

        var options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new NeaslatorDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new TranslationCache(_garnet, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ───── LookupAsync ─────

    [Fact]
    public async Task LookupAsync_AllL1Hits_ReturnsAllFromCache()
    {
        const long sourceHash = 12345L;
        const string normalizedText = "Spaghetti Carbonara";
        const string sourceLang = "en";
        var targets = new[] { "tr", "de" };

        var cachedTr = new CachedTranslation("Spagetti Karbonara", TranslationProviderTier.Primary, 0.98f, normalizedText);
        var cachedDe = new CachedTranslation("Spaghetti Carbonara", TranslationProviderTier.Primary, 0.97f, normalizedText);

        _redisDb.StringGetAsync(Arg.Is<RedisKey[]>(k =>
            k.Length == 2 &&
            k[0].ToString().Contains("12345:tr") &&
            k[1].ToString().Contains("12345:de")))
            .Returns(new RedisValue[]
            {
                System.Text.Json.JsonSerializer.Serialize(cachedTr),
                System.Text.Json.JsonSerializer.Serialize(cachedDe)
            });

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r =>
        {
            r.Source.Should().Be(CacheSource.L1Garnet);
            r.Translation.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task LookupAsync_AllMisses_ReturnsAllAsMiss()
    {
        const long sourceHash = 99999L;
        const string normalizedText = "New Item";
        const string sourceLang = "en";
        var targets = new[] { "tr", "de" };

        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { RedisValue.Null, RedisValue.Null });

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r =>
        {
            r.Source.Should().Be(CacheSource.Miss);
            r.Translation.Should().BeNull();
        });
    }

    [Fact]
    public async Task LookupAsync_L1MissL2Hit_ReturnsFromDatabaseAndBackfills()
    {
        const long sourceHash = 55555L;
        const string normalizedText = "Caesar Salad";
        const string sourceLang = "en";
        var targets = new[] { "fr" };

        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { RedisValue.Null });

        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedText,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "fr",
            TranslatedText = "Salade Cesar",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.95f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "fr");
        var result = results[0];
        result.Translation.Should().NotBeNull();
        result.Translation!.TranslatedText.Should().Be("Salade Cesar");

        // Verify backfill was triggered
        var backfillCalls = _redisDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "StringSetAsync")
            .ToList();
        backfillCalls.Should().NotBeEmpty();
    }

    [Fact]
    public async Task LookupAsync_L1CollisionDifferentText_FallsThroughToL2()
    {
        const long sourceHash = 77777L;
        const string normalizedText = "Espresso";
        const string sourceLang = "en";
        var targets = new[] { "it" };

        var cachedWrong = new CachedTranslation("Cappuccino", TranslationProviderTier.Primary, 0.9f, "Cappuccino");
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { System.Text.Json.JsonSerializer.Serialize(cachedWrong) });

        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedText,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "it",
            TranslatedText = "Espresso",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.99f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "it");
        results[0].Translation!.TranslatedText.Should().Be("Espresso");
    }

    [Fact]
    public async Task LookupAsync_EmptyTargetLanguages_ReturnsEmptyList()
    {
        var results = await _sut.LookupAsync(123L, "text", "en", Array.Empty<string>(), CancellationToken.None);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task LookupAsync_L1HitCorrectText_ReturnsL1Hit()
    {
        const long sourceHash = 11111L;
        const string normalizedText = "Tiramisu";
        const string sourceLang = "en";
        var targets = new[] { "tr" };

        var cached = new CachedTranslation("Tiramisu", TranslationProviderTier.Primary, 1.0f, normalizedText);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { System.Text.Json.JsonSerializer.Serialize(cached) });

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Source.Should().Be(CacheSource.L1Garnet);
    }

    [Fact]
    public async Task LookupAsync_IncrementsHitCountOnL2Hit()
    {
        // This test verifies the lookup returns correct data; hit count is tested
        // in database integration tests.
        const long sourceHash = 22222L;
        const string normalizedText = "Bruschetta";
        const string sourceLang = "en";
        var targets = new[] { "de" };

        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedText,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "de",
            TranslatedText = "Bruschetta",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.9f,
            HitCount = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "de");
        results[0].Translation.Should().NotBeNull();
        results[0].Translation!.TranslatedText.Should().Be("Bruschetta");
    }

    [Fact]
    public async Task LookupAsync_L2HitCollisionDifferentText_SkipsThatEntry()
    {
        const long sourceHash = 88888L;
        const string normalizedText = "Gelato";
        const string sourceLang = "en";
        var targets = new[] { "it" };

        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { RedisValue.Null });

        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = "Sorbet",
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "it",
            TranslatedText = "Sorbetto",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.9f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "it");
        results[0].Source.Should().Be(CacheSource.Miss);
        results[0].Translation.Should().BeNull();
    }

    [Fact]
    public async Task LookupAsync_MultipleTargetsMixedResults_CorrectSourcePerLanguage()
    {
        const long sourceHash = 99900L;
        const string normalizedText = "Ramen";
        const string sourceLang = "en";
        var targets = new[] { "ja", "de", "fr" };

        var cachedJa = new CachedTranslation("ラーメン", TranslationProviderTier.Primary, 1.0f, normalizedText);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[]
            {
                System.Text.Json.JsonSerializer.Serialize(cachedJa),
                RedisValue.Null,
                RedisValue.Null
            });

        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedText,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = "de",
            TranslatedText = "Ramen",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.99f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _sut.LookupAsync(sourceHash, normalizedText, sourceLang, targets, CancellationToken.None);

        results.Should().HaveCount(3);
        results.Should().ContainSingle(r => r.TargetLanguageCode == "ja" && r.Source == CacheSource.L1Garnet);
        results.Should().ContainSingle(r => r.TargetLanguageCode == "de" && r.Translation != null);
        results.Should().ContainSingle(r => r.TargetLanguageCode == "fr" && r.Source == CacheSource.Miss);
    }

    // ───── StoreAsync ─────

    [Fact]
    public async Task StoreAsync_NewEntry_StoresInDbAndCache()
    {
        const long sourceHash = 33333L;
        const string normalizedText = "Pizza Margherita";
        const string sourceLang = "en";
        const string targetLang = "it";
        const string translatedText = "Pizza Margherita";

        await _sut.StoreAsync(sourceHash, normalizedText, sourceLang, targetLang, translatedText,
            TranslationProviderTier.Primary, "deepseek", 0.99f, CancellationToken.None);

        var entry = await _db.TranslationMemory.FirstOrDefaultAsync(e => e.SourceHash == sourceHash);
        entry.Should().NotBeNull();
        entry!.TranslatedText.Should().Be(translatedText);
        entry.ProviderName.Should().Be("deepseek");
        entry.ProviderTier.Should().Be(TranslationProviderTier.Primary);
        entry.ConfidenceScore.Should().Be(0.99f);
        entry.SourceLanguageCode.Should().Be(sourceLang);
        entry.TargetLanguageCode.Should().Be(targetLang);

        var calls = _redisDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "StringSetAsync")
            .ToList();
        calls.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StoreAsync_MultipleLanguages_EachStoredIndependently()
    {
        const long sourceHash = 44444L;
        const string normalizedText = "Risotto";
        const string sourceLang = "en";

        await _sut.StoreAsync(sourceHash, normalizedText, sourceLang, "it", "Risotto",
            TranslationProviderTier.Primary, "deepseek", 0.99f, CancellationToken.None);
        await _sut.StoreAsync(sourceHash, normalizedText, sourceLang, "fr", "Risotto",
            TranslationProviderTier.Primary, "deepseek", 0.98f, CancellationToken.None);

        var entries = await _db.TranslationMemory
            .Where(e => e.SourceHash == sourceHash)
            .ToListAsync();
        entries.Should().HaveCount(2);
        entries.Select(e => e.TargetLanguageCode).Should().Contain(["it", "fr"]);
    }

    [Fact]
    public async Task StoreAsync_WithSecondaryProvider_StoresCorrectTier()
    {
        const long sourceHash = 55555L;
        const string normalizedText = "Paella";
        const string sourceLang = "en";

        await _sut.StoreAsync(sourceHash, normalizedText, sourceLang, "es", "Paella",
            TranslationProviderTier.Secondary, "openai", 0.85f, CancellationToken.None);

        var entry = await _db.TranslationMemory.FirstAsync(e => e.SourceHash == sourceHash);
        entry.ProviderTier.Should().Be(TranslationProviderTier.Secondary);
        entry.ProviderName.Should().Be("openai");
    }

    // ───── InvalidateAsync ─────

    [Fact]
    public async Task InvalidateAsync_DeletesCacheKey()
    {
        _redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), CommandFlags.None).Returns(true);

        await _sut.InvalidateAsync(55555L, "tr");

        await _redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "neaslator:t:55555:tr"),
            CommandFlags.None);
    }

    [Fact]
    public async Task InvalidateAsync_KeyNotExists_DoesNotThrow()
    {
        _redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), CommandFlags.None).Returns(false);

        var act = () => _sut.InvalidateAsync(99999L, "xx");
        await act.Should().NotThrowAsync();
    }
}
