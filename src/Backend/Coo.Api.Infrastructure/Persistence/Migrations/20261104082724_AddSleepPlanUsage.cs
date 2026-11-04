using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddSleepPlanUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "plan_count",
                table: "advisor_usages",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "plan_count",
                table: "advisor_usages");
        }
    }
}
