using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610120001_AddQuickNotes")]
public sealed class AddQuickNotes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "QuickNotes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Content = table.Column<string>(type: "nvarchar(20000)", maxLength: 20000, nullable: false),
                IsPinned = table.Column<bool>(type: "bit", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_QuickNotes", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_QuickNotes_IsPinned_UpdatedUtc",
            table: "QuickNotes",
            columns: new[] { "IsPinned", "UpdatedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "QuickNotes");
}
