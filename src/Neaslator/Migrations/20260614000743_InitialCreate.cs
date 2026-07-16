using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Neaslator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_translation_memory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    SourceHash = table.Column<long>(type: "bigint", nullable: false),
                    NormalizedSourceText = table.Column<string>(type: "text", nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TranslatedText = table.Column<string>(type: "text", nullable: false),
                    ProviderTier = table.Column<short>(type: "smallint", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfidenceScore = table.Column<float>(type: "real", nullable: false, defaultValue: 1f),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    HitCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_translation_memory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "menu_publish_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MenuId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_menu_publish_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "supported_languages",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EnglishName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NativeName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supported_languages", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gtm_lookup",
                table: "global_translation_memory",
                columns: new[] { "SourceHash", "TargetLanguageCode" })
                .Annotation("Npgsql:IndexInclude", new[] { "NormalizedSourceText", "TranslatedText", "ProviderTier", "ConfidenceScore" });

            migrationBuilder.CreateIndex(
                name: "ix_gtm_quality_upgrade",
                table: "global_translation_memory",
                columns: new[] { "ProviderTier", "UpdatedAt" },
                filter: "\"ProviderTier\" > 0");

            migrationBuilder.CreateIndex(
                name: "uq_source_target",
                table: "global_translation_memory",
                columns: new[] { "SourceHash", "SourceLanguageCode", "TargetLanguageCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_snapshot_menu",
                table: "menu_publish_snapshots",
                column: "MenuId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_translation_memory");

            migrationBuilder.DropTable(
                name: "menu_publish_snapshots");

            migrationBuilder.DropTable(
                name: "supported_languages");
        }
    }
}
