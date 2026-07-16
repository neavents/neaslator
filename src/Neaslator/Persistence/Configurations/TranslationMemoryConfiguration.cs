using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class TranslationMemoryConfiguration : IEntityTypeConfiguration<TranslationMemoryEntry>
{
    public void Configure(EntityTypeBuilder<TranslationMemoryEntry> builder)
    {
        builder.ToTable("global_translation_memory");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(e => e.SourceHash).IsRequired();
        builder.Property(e => e.NormalizedSourceText).IsRequired();
        builder.Property(e => e.SourceLanguageCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TargetLanguageCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TranslatedText).IsRequired();
        builder.Property(e => e.ProviderTier).IsRequired();
        builder.Property(e => e.ProviderName).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ConfidenceScore).HasDefaultValue(1.0f);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.HitCount).HasDefaultValue(0L);

        builder.HasIndex(e => new { e.SourceHash, e.SourceLanguageCode, e.TargetLanguageCode })
            .IsUnique()
            .HasDatabaseName("uq_source_target");

        builder.HasIndex(e => new { e.SourceHash, e.TargetLanguageCode })
            .HasDatabaseName("ix_gtm_lookup")
            .IncludeProperties(e => new
            {
                e.NormalizedSourceText,
                e.TranslatedText,
                e.ProviderTier,
                e.ConfidenceScore
            });

        builder.HasIndex(e => new { e.ProviderTier, e.UpdatedAt })
            .HasDatabaseName("ix_gtm_quality_upgrade")
            .HasFilter("\"ProviderTier\" > 0");
    }
}
