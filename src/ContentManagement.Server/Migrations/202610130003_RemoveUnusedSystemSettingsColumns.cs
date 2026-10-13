using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610130003_RemoveUnusedSystemSettingsColumns")]
public sealed class RemoveUnusedSystemSettingsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SmtpHost", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpPort", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpUseSsl", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpUsername", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpPassword", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpFromEmail", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "SmtpFromName", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "TestRecipientEmail", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "CleanupIntervalHours", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "DeletionGracePeriodDays", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "UpdatedUtc", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "LastCleanupUtc", table: "SystemSettings");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SmtpHost", table: "SystemSettings",
            type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>(
            name: "SmtpPort", table: "SystemSettings",
            type: "int", nullable: false, defaultValue: 587);
        migrationBuilder.AddColumn<bool>(
            name: "SmtpUseSsl", table: "SystemSettings",
            type: "bit", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>(
            name: "SmtpUsername", table: "SystemSettings",
            type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "SmtpPassword", table: "SystemSettings",
            type: "nvarchar(2048)", maxLength: 2048, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "SmtpFromEmail", table: "SystemSettings",
            type: "nvarchar(254)", maxLength: 254, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "SmtpFromName", table: "SystemSettings",
            type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: "ContentManagement");
        migrationBuilder.AddColumn<string>(
            name: "TestRecipientEmail", table: "SystemSettings",
            type: "nvarchar(254)", maxLength: 254, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>(
            name: "CleanupIntervalHours", table: "SystemSettings",
            type: "int", nullable: false, defaultValue: 24);
        migrationBuilder.AddColumn<int>(
            name: "DeletionGracePeriodDays", table: "SystemSettings",
            type: "int", nullable: false, defaultValue: 90);
        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedUtc", table: "SystemSettings",
            type: "datetime2", nullable: false, defaultValue: new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        migrationBuilder.AddColumn<DateTime>(
            name: "LastCleanupUtc", table: "SystemSettings",
            type: "datetime2", nullable: true);
    }
}
