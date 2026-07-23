using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Persistence;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace Neaslator.Tests.Cache;

/// <summary>
/// Integration coverage against a real PostgreSQL instance for the one branch SQLite
/// cannot reach: the duplicate-key (SQLSTATE 23505) upsert path in
/// <see cref="TranslationCache.StoreAsync"/>. Requires Docker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TranslationCachePostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly IDatabase _redis = Substitute.For<IDatabase>();
    private NeaslatorDbContext _db = null!;
    private TranslationCache _sut = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redis);

        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _db = new NeaslatorDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _sut = new TranslationCache(garnet, _db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private NeaslatorDbContext FreshContext()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new NeaslatorDbContext(options);
    }

    [Fact]
    public async Task StoreAsync_DuplicateKey_UpsertsInPlace()
    {
        const long hash = 5150L;
        await _sut.StoreAsync(hash, "Negroni", "en", "it", "Negroni",
            TranslationProviderTier.Degraded, "google", 0.6f, CancellationToken.None);

        await _sut.StoreAsync(hash, "Negroni", "en", "it", "Negroni (aggiornato)",
            TranslationProviderTier.Primary, "deepseek", 0.99f, CancellationToken.None);

        await using NeaslatorDbContext verify = FreshContext();
        List<TranslationMemoryEntry> rows = await verify.TranslationMemory
            .Where(e => e.SourceHash == hash && e.SourceLanguageCode == "en" && e.TargetLanguageCode == "it")
            .AsNoTracking()
            .ToListAsync();

        rows.Should().ContainSingle("the unique (hash, source, target) key must upsert, not duplicate");
        rows[0].TranslatedText.Should().Be("Negroni (aggiornato)");
        rows[0].ProviderTier.Should().Be(TranslationProviderTier.Primary);
        rows[0].ProviderName.Should().Be("deepseek");
        rows[0].ConfidenceScore.Should().Be(0.99f);
    }

    [Fact]
    public async Task StoreAsync_SameHashDifferentTarget_InsertsSeparateRows()
    {
        const long hash = 6161L;
        await _sut.StoreAsync(hash, "Espresso", "en", "it", "Espresso",
            TranslationProviderTier.Primary, "deepseek", 1.0f, CancellationToken.None);
        await _sut.StoreAsync(hash, "Espresso", "en", "de", "Espresso",
            TranslationProviderTier.Primary, "deepseek", 1.0f, CancellationToken.None);

        await using NeaslatorDbContext verify = FreshContext();
        int count = await verify.TranslationMemory.CountAsync(e => e.SourceHash == hash);
        count.Should().Be(2);
    }

    [Fact]
    public async Task StoreThenLookup_ReturnsFromL2AndIncrementsHitCount()
    {
        const long hash = 7272L;
        await _sut.StoreAsync(hash, "Ravioli", "en", "fr", "Raviolis",
            TranslationProviderTier.Primary, "deepseek", 0.95f, CancellationToken.None);

        // L1 misses -> forces the real L2 (Postgres) read + ExecuteUpdate hit-count bump.
        _redis.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        IReadOnlyList<CacheLookupResult> results = await _sut.LookupAsync(
            hash, "Ravioli", "en", new[] { "fr" }, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Source.Should().Be(CacheSource.L2PostgreSql);
        results[0].Translation!.TranslatedText.Should().Be("Raviolis");

        await using NeaslatorDbContext verify = FreshContext();
        TranslationMemoryEntry entry = await verify.TranslationMemory.AsNoTracking()
            .FirstAsync(e => e.SourceHash == hash && e.TargetLanguageCode == "fr");
        entry.HitCount.Should().Be(1);
    }

    [Fact]
    public async Task StoreAsync_Upsert_DoesNotResetHitCount()
    {
        const long hash = 8383L;
        await _sut.StoreAsync(hash, "Gnocchi", "en", "fr", "Gnocchis",
            TranslationProviderTier.Secondary, "openai", 0.8f, CancellationToken.None);

        // Bump the hit count via an L2 lookup.
        _redis.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });
        await _sut.LookupAsync(hash, "Gnocchi", "en", new[] { "fr" }, CancellationToken.None);

        // Upsert must only touch the columns it sets, leaving HitCount intact.
        await _sut.StoreAsync(hash, "Gnocchi", "en", "fr", "Gnocchis (v2)",
            TranslationProviderTier.Primary, "deepseek", 0.99f, CancellationToken.None);

        await using NeaslatorDbContext verify = FreshContext();
        TranslationMemoryEntry entry = await verify.TranslationMemory.AsNoTracking()
            .FirstAsync(e => e.SourceHash == hash && e.TargetLanguageCode == "fr");
        entry.HitCount.Should().Be(1);
        entry.TranslatedText.Should().Be("Gnocchis (v2)");
    }
}
