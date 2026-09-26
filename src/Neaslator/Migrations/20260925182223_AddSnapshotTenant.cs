using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neaslator.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "menu_publish_snapshots",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "menu_publish_snapshots");
        }
    }
}
