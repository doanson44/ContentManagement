using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610090005_AddSelfServiceRegistration")]
public sealed class AddSelfServiceRegistration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RegistrationTokenHash",
            table: "ManagedUsers",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "RegistrationExpiresUtc",
            table: "ManagedUsers",
            type: "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ManagedUsers_Status_RegistrationExpiresUtc",
            table: "ManagedUsers",
            columns: new[] { "Status", "RegistrationExpiresUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ManagedUsers_Status_RegistrationExpiresUtc",
            table: "ManagedUsers");

        migrationBuilder.DropColumn(name: "RegistrationTokenHash", table: "ManagedUsers");
        migrationBuilder.DropColumn(name: "RegistrationExpiresUtc", table: "ManagedUsers");
    }
}
