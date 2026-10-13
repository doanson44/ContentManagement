using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610130004_RemoveUnusedOtpLastSentUtc")]
public sealed class RemoveUnusedOtpLastSentUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LastSentUtc", table: "AdminOtpChallenges");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "LastSentUtc", table: "AdminOtpChallenges", type: "datetime2", nullable: true);
    }
}
