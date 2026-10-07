using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mendeleev.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_settings",
                schema: "public",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_by_staff_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_settings", x => x.key);
                    table.ForeignKey(
                        name: "fk_service_settings_staff_updated_by_staff_id",
                        column: x => x.updated_by_staff_id,
                        principalSchema: "public",
                        principalTable: "staff",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_settings_updated_by_staff_id",
                schema: "public",
                table: "service_settings",
                column: "updated_by_staff_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_settings",
                schema: "public");
        }
    }
}
