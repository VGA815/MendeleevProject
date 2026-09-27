using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mendeleev.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    staff_id = table.Column<long>(type: "bigint", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_user_id = table.Column<long>(type: "bigint", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "broadcasts",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    segment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    with_update_button = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    sent = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    bot_blocked = table.Column<int>(type: "integer", nullable: false),
                    cursor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_by_staff_id = table.Column<long>(type: "bigint", nullable: false),
                    report_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broadcasts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_events",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_telegram_updates",
                schema: "public",
                columns: table => new
                {
                    update_id = table.Column<long>(type: "bigint", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_telegram_updates", x => x.update_id);
                });

            migrationBuilder.CreateTable(
                name: "staff",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_staff_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tariffs",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    period_days = table.Column<int>(type: "integer", nullable: false),
                    device_limit = table.Column<int>(type: "integer", nullable: false),
                    traffic_limit_bytes = table.Column<long>(type: "bigint", nullable: true),
                    panel_squads = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tariffs", x => x.id);
                    table.CheckConstraint("ck_tariffs_device_limit", "device_limit > 0");
                    table.CheckConstraint("ck_tariffs_period_days", "period_days > 0");
                    table.CheckConstraint("ck_tariffs_price", "price >= 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    email = table.Column<string>(type: "citext", maxLength: 254, nullable: true),
                    account_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    account_key_issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    trial_used = table.Column<bool>(type: "boolean", nullable: false),
                    trial_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    bot_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_resets",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    initiated_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    staff_id = table.Column<long>(type: "bigint", nullable: true),
                    devices_removed = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_resets", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_resets_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    tariff_id = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    days_granted = table.Column<int>(type: "integer", nullable: false),
                    confirmation_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    link_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    receipt_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    last_checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_payments_days_granted", "days_granted > 0");
                    table.ForeignKey(
                        name: "fk_payments_tariffs_tariff_id",
                        column: x => x.tariff_id,
                        principalSchema: "public",
                        principalTable: "tariffs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    tariff_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expired_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    panel_user_id = table.Column<int>(type: "integer", nullable: true),
                    panel_short_uuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    panel_vless_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    access_issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    first_connected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sync_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_tariffs_tariff_id",
                        column: x => x.tariff_id,
                        principalSchema: "public",
                        principalTable: "tariffs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "traffic_daily",
                schema: "public",
                columns: table => new
                {
                    subscription_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    lifetime_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_traffic_daily", x => new { x.subscription_id, x.day });
                    table.CheckConstraint("ck_traffic_daily_bytes", "bytes >= 0");
                    table.ForeignKey(
                        name: "fk_traffic_daily_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "public",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_action_created_at",
                schema: "public",
                table: "audit_log",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_staff_id_id",
                schema: "public",
                table: "audit_log",
                columns: new[] { "staff_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_target_user_id_id",
                schema: "public",
                table: "audit_log",
                columns: new[] { "target_user_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_broadcasts_status",
                schema: "public",
                table: "broadcasts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_device_resets_user_id_created_at",
                schema: "public",
                table: "device_resets",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                schema: "public",
                table: "notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_dedup_key",
                schema: "public",
                table: "notifications",
                column: "dedup_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id",
                schema: "public",
                table: "notifications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at",
                schema: "public",
                table: "outbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_next_attempt_at",
                schema: "public",
                table: "outbox_messages",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_events_created_at",
                schema: "public",
                table: "payment_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_payment_events_payment_id_id",
                schema: "public",
                table: "payment_events",
                columns: new[] { "payment_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_provider_provider_payment_id",
                schema: "public",
                table: "payments",
                columns: new[] { "provider", "provider_payment_id" },
                unique: true,
                filter: "provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_status_created_at",
                schema: "public",
                table: "payments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tariff_id",
                schema: "public",
                table: "payments",
                column: "tariff_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_user_id_created_at",
                schema: "public",
                table: "payments",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_telegram_updates_received_at",
                schema: "public",
                table: "processed_telegram_updates",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_staff_telegram_id",
                schema: "public",
                table: "staff",
                column: "telegram_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_panel_short_uuid",
                schema: "public",
                table: "subscriptions",
                column: "panel_short_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_panel_user_id",
                schema: "public",
                table: "subscriptions",
                column: "panel_user_id",
                unique: true,
                filter: "panel_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_status_expires_at",
                schema: "public",
                table: "subscriptions",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tariff_id",
                schema: "public",
                table: "subscriptions",
                column: "tariff_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_user_id",
                schema: "public",
                table: "subscriptions",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tariffs_code",
                schema: "public",
                table: "tariffs",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_traffic_daily_day",
                schema: "public",
                table: "traffic_daily",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_users_account_key_hash",
                schema: "public",
                table: "users",
                column: "account_key_hash",
                unique: true,
                filter: "account_key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_created_at",
                schema: "public",
                table: "users",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "public",
                table: "users",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_telegram_id",
                schema: "public",
                table: "users",
                column: "telegram_id",
                unique: true,
                filter: "telegram_id IS NOT NULL");

            // FR-ADM-07: the audit log is append-only. Whatever role the application runs under, recent
            // rows can be neither changed nor deleted; only the retention job may remove rows older than
            // a year (ТЗ 12, «Хранение и очистка»).
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.audit_log_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        RAISE EXCEPTION 'audit_log is append-only';
                    END IF;
                    IF TG_OP = 'DELETE' AND OLD.created_at > now() - interval '365 days' THEN
                        RAISE EXCEPTION 'audit_log rows younger than a year cannot be deleted';
                    END IF;
                    RETURN OLD;
                END;
                $$;

                CREATE TRIGGER audit_log_guard
                BEFORE UPDATE OR DELETE ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.audit_log_guard();
                """);

            // When the runtime role exists (deploy/postgres/init), it gets no UPDATE on the audit log at all.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mendeleev_app') THEN
                        REVOKE UPDATE ON public.audit_log FROM mendeleev_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_guard ON public.audit_log; DROP FUNCTION IF EXISTS public.audit_log_guard();");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "public");

            migrationBuilder.DropTable(
                name: "broadcasts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "device_resets",
                schema: "public");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "public");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "public");

            migrationBuilder.DropTable(
                name: "payment_events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "public");

            migrationBuilder.DropTable(
                name: "processed_telegram_updates",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff",
                schema: "public");

            migrationBuilder.DropTable(
                name: "traffic_daily",
                schema: "public");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tariffs",
                schema: "public");

            migrationBuilder.DropTable(
                name: "users",
                schema: "public");
        }
    }
}
