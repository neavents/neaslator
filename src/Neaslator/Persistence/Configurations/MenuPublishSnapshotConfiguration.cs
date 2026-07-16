using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class MenuPublishSnapshotConfiguration : IEntityTypeConfiguration<MenuPublishSnapshot>
{
    public void Configure(EntityTypeBuilder<MenuPublishSnapshot> builder)
    {
        builder.ToTable("menu_publish_snapshots");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.MenuId).IsRequired();
        builder.Property(e => e.OwnerId).IsRequired();
        builder.Property(e => e.SnapshotJson).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.PublishedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => e.MenuId)
            .IsUnique()
            .HasDatabaseName("uq_snapshot_menu");
    }
}
