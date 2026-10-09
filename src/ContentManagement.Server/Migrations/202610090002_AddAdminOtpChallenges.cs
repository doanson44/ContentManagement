using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610090002_AddAdminOtpChallenges")]
public sealed class AddAdminOtpChallenges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminOtpChallenges",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ConsumedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                FailedAttempts = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AdminOtpChallenges", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AdminOtpChallenges_Email_ConsumedUtc_ExpiresUtc",
            table: "AdminOtpChallenges",
            columns: new[] { "Email", "ConsumedUtc", "ExpiresUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_AdminOtpChallenges_Email_CreatedUtc",
            table: "AdminOtpChallenges",
            columns: new[] { "Email", "CreatedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AdminOtpChallenges");
}
