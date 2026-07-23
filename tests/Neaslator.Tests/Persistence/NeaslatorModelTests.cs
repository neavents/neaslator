using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Neaslator.Domain.Entities;
using Neaslator.Persistence;

namespace Neaslator.Tests.Persistence;

/// <summary>
/// Asserts the EF model is configured as the runtime relies on — the unique keys that make
/// the cache upsert correct, the filtered quality-upgrade index, jsonb storage, and the
/// global Ulid-to-Guid conversion. Inspects model metadata only; no database connection.
/// </summary>
public sealed class NeaslatorModelTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x")
            .Options;
        using var ctx = new NeaslatorDbContext(options);
        return ctx.Model;
    }

    private static IEntityType Entity<T>() => Model.FindEntityType(typeof(T))!;

    [Fact]
    public void TranslationMemory_MapsToExpectedTable()
    {
        Entity<TranslationMemoryEntry>().GetTableName().Should().Be("global_translation_memory");
    }

    [Fact]
    public void TranslationMemory_HasUniqueKeyOnSourceAndLanguages()
    {
        IIndex? unique = Entity<TranslationMemoryEntry>().GetIndexes()
            .FirstOrDefault(i => i.IsUnique);

        unique.Should().NotBeNull();
        unique!.Properties.Select(p => p.Name).Should()
            .ContainInOrder(nameof(TranslationMemoryEntry.SourceHash),
                            nameof(TranslationMemoryEntry.SourceLanguageCode),
                            nameof(TranslationMemoryEntry.TargetLanguageCode));
    }

    [Fact]
    public void TranslationMemory_HasFilteredQualityUpgradeIndex()
    {
        bool hasFilteredIndex = Entity<TranslationMemoryEntry>().GetIndexes()
            .Any(i => i.GetFilter() is not null && i.GetFilter()!.Contains("ProviderTier"));

        hasFilteredIndex.Should().BeTrue("degraded entries are scanned via a partial index");
    }

    [Fact]
    public void MenuPublishSnapshot_HasUniqueIndexOnMenuId()
    {
        IEntityType entity = Entity<MenuPublishSnapshot>();
        entity.GetTableName().Should().Be("menu_publish_snapshots");
        entity.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(MenuPublishSnapshot.MenuId));
    }

    [Fact]
    public void MenuPublishSnapshot_JsonStoredAsJsonb()
    {
        IProperty json = Entity<MenuPublishSnapshot>().FindProperty(nameof(MenuPublishSnapshot.SnapshotJson))!;
        json.GetColumnType().Should().Be("jsonb");
    }

    [Fact]
    public void SupportedLanguage_KeyIsCode()
    {
        IKey key = Entity<SupportedLanguage>().FindPrimaryKey()!;
        key.Properties.Should().ContainSingle().Which.Name.Should().Be(nameof(SupportedLanguage.Code));
    }

    [Fact]
    public void UlidProperties_UseGuidConverter()
    {
        IProperty menuId = Entity<MenuPublishSnapshot>().FindProperty(nameof(MenuPublishSnapshot.MenuId))!;
        menuId.GetValueConverter().Should().BeOfType<UlidToGuidConverter>();
        menuId.GetValueConverter()!.ProviderClrType.Should().Be(typeof(Guid));
    }

    [Fact]
    public void Model_HasNoPendingChangesNotCapturedInAMigration()
    {
        // Guards against the classic "changed the model but forgot to add a migration".
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x")
            .Options;
        using var ctx = new NeaslatorDbContext(options);

        ctx.Database.HasPendingModelChanges().Should().BeFalse(
            "the EF model must match the latest migration snapshot");
    }
}
