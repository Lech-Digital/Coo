using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddBabyAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "baby_avatars",
                columns: table => new
                {
                    baby_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    avatar_type = table.Column<string>(type: "text", nullable: false),
                    preset_key = table.Column<string>(type: "text", nullable: true),
                    image_data = table.Column<byte[]>(type: "bytea", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baby_avatars", x => x.baby_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_baby_avatars_household",
                table: "baby_avatars",
                column: "household_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "baby_avatars");
        }
    }
}
