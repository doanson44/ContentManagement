using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610190001_AddPublicHtmlPages")]
public sealed class AddPublicHtmlPages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicHtmlPages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Slug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                StorageKey = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastViewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                StorageDeletePending = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
            },
            constraints: table => table.PrimaryKey("PK_PublicHtmlPages", item => item.Id));

        migrationBuilder.CreateIndex("IX_PublicHtmlPages_Slug", "PublicHtmlPages", "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_PublicHtmlPages_IsPublished_IsDeleted_CreatedUtc", "PublicHtmlPages", new[] { "IsPublished", "IsDeleted", "CreatedUtc" });
        migrationBuilder.CreateIndex("IX_PublicHtmlPages_LastViewedAtUtc", "PublicHtmlPages", "LastViewedAtUtc");
        migrationBuilder.CreateIndex("IX_PublicHtmlPages_StorageDeletePending", "PublicHtmlPages", "StorageDeletePending");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PublicHtmlPages");
}
