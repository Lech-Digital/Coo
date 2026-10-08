using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddAdvisorUsages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "advisor_usages",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    question_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advisor_usages", x => new { x.user_id, x.date });
                    table.ForeignKey(
                        name: "fk_advisor_usages_users",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "advisor_usages");
        }
    }
}
