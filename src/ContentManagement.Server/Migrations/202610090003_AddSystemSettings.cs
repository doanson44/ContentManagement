using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610090003_AddSystemSettings")]
public sealed class AddSystemSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SystemSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                SmtpHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                SmtpPort = table.Column<int>(type: "int", nullable: false),
                SmtpUseSsl = table.Column<bool>(type: "bit", nullable: false),
                SmtpUsername = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                SmtpPassword = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                SmtpFromEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                SmtpFromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                TestRecipientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                StaleFileAgeDays = table.Column<int>(type: "int", nullable: false),
                CleanupIntervalHours = table.Column<int>(type: "int", nullable: false),
                DeletionGracePeriodDays = table.Column<int>(type: "int", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastCleanupUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_SystemSettings", x => x.Id));

    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SystemSettings");
}
