using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence;

public sealed class NeaslatorDbContext(DbContextOptions<NeaslatorDbContext> options)
    : DbContext(options)
{
    public DbSet<TranslationMemoryEntry> TranslationMemory => Set<TranslationMemoryEntry>();
    public DbSet<SupportedLanguage> SupportedLanguages => Set<SupportedLanguage>();
    public DbSet<MenuPublishSnapshot> MenuPublishSnapshots => Set<MenuPublishSnapshot>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Ulid>()
            .HaveConversion<UlidToGuidConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NeaslatorDbContext).Assembly);
    }
}
