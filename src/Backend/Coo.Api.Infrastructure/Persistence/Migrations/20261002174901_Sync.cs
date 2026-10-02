using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Sync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "memberships",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "applied_changes",
                columns: table => new
                {
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    applied_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applied_changes", x => x.change_id);
                    table.CheckConstraint("ck_applied_changes_result", "result IN ('applied', 'superseded', 'rejected')");
                });

            migrationBuilder.CreateTable(
                name: "baby_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    nicknames = table.Column<List<string>>(type: "text[]", nullable: false),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    version_wall_ms = table.Column<long>(type: "bigint", nullable: false),
                    version_counter = table.Column<int>(type: "integer", nullable: false),
                    version_device = table.Column<Guid>(type: "uuid", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baby_profiles", x => x.id);
                    table.CheckConstraint("ck_baby_profiles_date", "birth_date IS NOT NULL OR due_date IS NOT NULL");
                    table.CheckConstraint("ck_baby_profiles_seq", "seq >= 1");
                    table.ForeignKey(
                        name: "fk_baby_profiles_households",
                        column: x => x.household_id,
                        principalTable: "households",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consents",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    wording = table.Column<string>(type: "text", nullable: false),
                    given_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    app_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consents", x => new { x.user_id, x.purpose, x.wording });
                    table.ForeignKey(
                        name: "fk_consents_users",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_dismissals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_a = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_b = table.Column<Guid>(type: "uuid", nullable: false),
                    version_wall_ms = table.Column<long>(type: "bigint", nullable: false),
                    version_counter = table.Column<int>(type: "integer", nullable: false),
                    version_device = table.Column<Guid>(type: "uuid", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_dismissals", x => x.id);
                    table.CheckConstraint("ck_duplicate_dismissals_pair", "entry_a <> entry_b");
                    table.CheckConstraint("ck_duplicate_dismissals_seq", "seq >= 1");
                    table.ForeignKey(
                        name: "fk_duplicate_dismissals_households",
                        column: x => x.household_id,
                        principalTable: "households",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "household_settings",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    volume_unit = table.Column<string>(type: "text", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    hidden_kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    version_wall_ms = table.Column<long>(type: "bigint", nullable: false),
                    version_counter = table.Column<int>(type: "integer", nullable: false),
                    version_device = table.Column<Guid>(type: "uuid", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_household_settings", x => x.household_id);
                    table.CheckConstraint("ck_household_settings_seq", "seq >= 1");
                    table.CheckConstraint("ck_household_settings_unit", "volume_unit IN ('ml', 'oz')");
                    table.ForeignKey(
                        name: "fk_household_settings_households",
                        column: x => x.household_id,
                        principalTable: "households",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_conflicts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    winner_wall_ms = table.Column<long>(type: "bigint", nullable: false),
                    winner_counter = table.Column<int>(type: "integer", nullable: false),
                    winner_device = table.Column<Guid>(type: "uuid", nullable: false),
                    loser_wall_ms = table.Column<long>(type: "bigint", nullable: false),
                    loser_counter = table.Column<int>(type: "integer", nullable: false),
                    loser_device = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_conflicts", x => x.id);
                    table.ForeignKey(
                        name: "fk_sync_conflicts_households",
                        column: x => x.household_id,
                        principalTable: "households",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applied_changes_applied_at",
                table: "applied_changes",
                column: "applied_at");

            migrationBuilder.CreateIndex(
                name: "ix_baby_profiles_household_seq",
                table: "baby_profiles",
                columns: new[] { "household_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_duplicate_dismissals_household_seq",
                table: "duplicate_dismissals",
                columns: new[] { "household_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_household_settings_household_seq",
                table: "household_settings",
                columns: new[] { "household_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_household_id",
                table: "sync_conflicts",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "ix_sync_conflicts_recorded_at",
                table: "sync_conflicts",
                column: "recorded_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applied_changes");

            migrationBuilder.DropTable(
                name: "baby_profiles");

            migrationBuilder.DropTable(
                name: "consents");

            migrationBuilder.DropTable(
                name: "duplicate_dismissals");

            migrationBuilder.DropTable(
                name: "household_settings");

            migrationBuilder.DropTable(
                name: "sync_conflicts");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "memberships");
        }
    }
}
