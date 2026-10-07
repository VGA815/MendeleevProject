using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mendeleev.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPromoCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "selected_promo_code_id",
                schema: "public",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "promo_code_id",
                schema: "public",
                table: "payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promo_codes",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "citext", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    used_count = table.Column<int>(type: "integer", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    valid_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_staff_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promo_codes", x => x.id);
                    table.CheckConstraint("ck_promo_codes_max_uses", "max_uses IS NULL OR max_uses > 0");
                    table.CheckConstraint("ck_promo_codes_used_count", "used_count >= 0");
                    table.CheckConstraint("ck_promo_codes_value", "value > 0");
                    table.ForeignKey(
                        name: "fk_promo_codes_staff_created_by_staff_id",
                        column: x => x.created_by_staff_id,
                        principalSchema: "public",
                        principalTable: "staff",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "promo_redemptions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    promo_code_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bonus_days = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promo_redemptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_promo_redemptions_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "public",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_promo_redemptions_promo_codes_promo_code_id",
                        column: x => x.promo_code_id,
                        principalSchema: "public",
                        principalTable: "promo_codes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_promo_redemptions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_selected_promo_code_id",
                schema: "public",
                table: "users",
                column: "selected_promo_code_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_promo_code_id",
                schema: "public",
                table: "payments",
                column: "promo_code_id");

            migrationBuilder.CreateIndex(
                name: "ix_promo_codes_code",
                schema: "public",
                table: "promo_codes",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promo_codes_created_by_staff_id",
                schema: "public",
                table: "promo_codes",
                column: "created_by_staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_promo_redemptions_payment_id",
                schema: "public",
                table: "promo_redemptions",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_promo_redemptions_promo_code_id_user_id",
                schema: "public",
                table: "promo_redemptions",
                columns: new[] { "promo_code_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promo_redemptions_user_id",
                schema: "public",
                table: "promo_redemptions",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_payments_promo_codes_promo_code_id",
                schema: "public",
                table: "payments",
                column: "promo_code_id",
                principalSchema: "public",
                principalTable: "promo_codes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_promo_codes_selected_promo_code_id",
                schema: "public",
                table: "users",
                column: "selected_promo_code_id",
                principalSchema: "public",
                principalTable: "promo_codes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payments_promo_codes_promo_code_id",
                schema: "public",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "fk_users_promo_codes_selected_promo_code_id",
                schema: "public",
                table: "users");

            migrationBuilder.DropTable(
                name: "promo_redemptions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "promo_codes",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "ix_users_selected_promo_code_id",
                schema: "public",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_payments_promo_code_id",
                schema: "public",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "selected_promo_code_id",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "promo_code_id",
                schema: "public",
                table: "payments");
        }
    }
}
