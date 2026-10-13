using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610130002_RemoveExternalStorageConnectionSettings")]
public sealed class RemoveExternalStorageConnectionSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExternalStorageBaseUrl",
            table: "SystemSettings");

        migrationBuilder.DropColumn(
            name: "ExternalStorageApiKeyProtected",
            table: "SystemSettings");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalStorageBaseUrl",
            table: "SystemSettings",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ExternalStorageApiKeyProtected",
            table: "SystemSettings",
            type: "nvarchar(4000)",
            maxLength: 4000,
            nullable: false,
            defaultValue: "");
    }
}
