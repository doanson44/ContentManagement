using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ContentManagement.Server.Data;

#nullable disable

namespace ContentManagement.Server.Data.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610120003_AddExternalStorageSettings")]
public sealed class AddExternalStorageSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalStorageBaseUrl", table: "SystemSettings",
            type: "nvarchar(500)", maxLength: 500, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "ExternalStorageApiKeyProtected", table: "SystemSettings",
            type: "nvarchar(4096)", maxLength: 4096, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<long>(
            name: "ExternalStorageQuotaBytes", table: "SystemSettings",
            type: "bigint", nullable: false, defaultValue: 10737418240L);
        migrationBuilder.AddColumn<bool>(
            name: "ExternalStoragePreferred", table: "SystemSettings",
            type: "bit", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ExternalStorageBaseUrl", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ExternalStorageApiKeyProtected", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ExternalStorageQuotaBytes", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ExternalStoragePreferred", table: "SystemSettings");
    }
}
