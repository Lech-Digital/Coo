using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coo.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddViewerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_memberships_role",
                table: "memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invitations_role",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_memberships_role",
                table: "memberships",
                sql: "role IN ('owner', 'parent', 'caregiver', 'viewer')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitations_role",
                table: "invitations",
                sql: "role IN ('parent', 'caregiver', 'viewer')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_memberships_role",
                table: "memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invitations_role",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_memberships_role",
                table: "memberships",
                sql: "role IN ('owner', 'parent', 'caregiver')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitations_role",
                table: "invitations",
                sql: "role IN ('parent', 'caregiver')");
        }
    }
}
