using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610290001_AddPinnedProxy")]
public sealed class AddPinnedProxy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PinnedProxies",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Address = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Protocol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                PinnedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastCheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LatencyMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                IsHealthy = table.Column<bool>(type: "bit", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_PinnedProxies", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PinnedProxies");
}