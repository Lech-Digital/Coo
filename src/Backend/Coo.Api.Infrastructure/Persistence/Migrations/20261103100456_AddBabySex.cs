using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddBabySex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sex",
                table: "baby_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_baby_profiles_sex",
                table: "baby_profiles",
                sql: "sex IS NULL OR sex IN ('girl', 'boy')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_baby_profiles_sex",
                table: "baby_profiles");

            migrationBuilder.DropColumn(
                name: "sex",
                table: "baby_profiles");
        }
    }
}
