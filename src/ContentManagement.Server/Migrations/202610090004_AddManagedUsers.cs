using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610090004_AddManagedUsers")]
public sealed class AddManagedUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ManagedUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                PermissionsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                InvitationTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                InvitationExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                InvitedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                ActivatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastLoginUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ManagedUsers", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ManagedUsers_Email",
            table: "ManagedUsers",
            column: "Email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ManagedUsers_Status_InvitationExpiresUtc",
            table: "ManagedUsers",
            columns: new[] { "Status", "InvitationExpiresUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ManagedUsers");
}
