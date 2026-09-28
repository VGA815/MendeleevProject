using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mendeleev.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWebCabinet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "session_stamp",
                schema: "public",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateTable(
                name: "link_codes",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_codes_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_code_hash",
                schema: "public",
                table: "link_codes",
                column: "code_hash");

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_expires_at",
                schema: "public",
                table: "link_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_user_id",
                schema: "public",
                table: "link_codes",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_codes",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "session_stamp",
                schema: "public",
                table: "users");
        }
    }
}
