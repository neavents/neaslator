using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Persistence;
using Testcontainers.PostgreSql;

namespace Neaslator.Tests.Persistence;

/// <summary>
/// Applies the real EF migrations (not EnsureCreated) to a fresh PostgreSQL instance to prove
/// they run cleanly end-to-end, produce a usable schema, and that the seed migration actually
/// populates supported languages. Requires Docker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NeaslatorMigrationsPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private NeaslatorDbContext CreateContext()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new NeaslatorDbContext(options);
    }

    [Fact]
    public async Task Migrations_ApplyCleanly_AndLeaveNoPending()
    {
        await using NeaslatorDbContext ctx = CreateContext();

        await ctx.Database.MigrateAsync();

        IEnumerable<string> applied = await ctx.Database.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task MigratedSchema_SupportsCacheWrite_ThroughUlidAndIdentityColumns()
    {
        await using NeaslatorDbContext ctx = CreateContext();
        await ctx.Database.MigrateAsync();

        ctx.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = 4242L,
            NormalizedSourceText = "Ravioli",
            SourceLanguageCode = "en",
            TargetLanguageCode = "fr",
            TranslatedText = "Raviolis",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.95f
            // CreatedAt/UpdatedAt/HitCount rely on DB defaults from the migration.
        });
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();
        TranslationMemoryEntry read = await ctx.TranslationMemory.AsNoTracking()
            .FirstAsync(e => e.SourceHash == 4242L);

        read.Id.Should().BeGreaterThan(0, "the identity column is assigned by the DB");
        read.TranslatedText.Should().Be("Raviolis");
        read.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task MigratedSchema_EnforcesUniqueSourceTargetKey()
    {
        await using NeaslatorDbContext ctx = CreateContext();
        await ctx.Database.MigrateAsync();

        TranslationMemoryEntry Make() => new()
        {
            SourceHash = 9001L,
            NormalizedSourceText = "Gnocchi",
            SourceLanguageCode = "en",
            TargetLanguageCode = "it",
            TranslatedText = "Gnocchi",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 1f
        };

        ctx.TranslationMemory.Add(Make());
        await ctx.SaveChangesAsync();

        await using NeaslatorDbContext ctx2 = CreateContext();
        ctx2.TranslationMemory.Add(Make());
        Func<Task> duplicate = () => ctx2.SaveChangesAsync();

        await duplicate.Should().ThrowAsync<DbUpdateException>(
            "uq_source_target must reject a duplicate (hash, source, target)");
    }

    [Fact]
    public async Task SeedMigration_PopulatesSupportedLanguages()
    {
        await using NeaslatorDbContext ctx = CreateContext();
        await ctx.Database.MigrateAsync();

        (await ctx.SupportedLanguages.CountAsync()).Should().BeGreaterThan(0,
            "the seed migration inserts the supported language set");
    }
}
