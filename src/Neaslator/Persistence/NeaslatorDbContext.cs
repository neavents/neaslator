using MassTransit;
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

        // MassTransit's transactional inbox and outbox tables.
        //
        // These MUST be mapped or the AddEntityFrameworkOutbox registration in Program.cs takes every
        // consumer in this service offline — the filter resolves a DbSet the model does not contain and
        // the endpoint fails to start. Identity's context carries the same warning for the same reason.
        //
        // InboxState deduplicates a redelivered message; OutboxState/OutboxMessage let an integration
        // event be written in the same transaction as the row it describes, so "translation completed"
        // and "MenuTranslationCompleted published" can no longer disagree.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
