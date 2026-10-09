using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610090001_InitialContentSchema")]
public sealed class InitialContentSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                StorageKey = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Files", x => x.Id));
        migrationBuilder.CreateTable(
            name: "JsonDocuments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DocumentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CompressedPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                UncompressedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                CompressedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_JsonDocuments", x => x.Id));
        migrationBuilder.CreateIndex("IX_Files_StorageKey", "Files", "StorageKey", unique: true);
        migrationBuilder.CreateIndex("IX_Files_Status_CreatedUtc", "Files", new[] { "Status", "CreatedUtc" });
        migrationBuilder.CreateIndex("IX_Files_OwnerId", "Files", "OwnerId");
        migrationBuilder.CreateIndex("IX_JsonDocuments_DocumentType_CreatedUtc", "JsonDocuments", new[] { "DocumentType", "CreatedUtc" });
        migrationBuilder.CreateIndex("IX_JsonDocuments_Status_UpdatedUtc", "JsonDocuments", new[] { "Status", "UpdatedUtc" });
        migrationBuilder.CreateIndex("IX_JsonDocuments_OwnerId", "JsonDocuments", "OwnerId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Files");
        migrationBuilder.DropTable("JsonDocuments");
    }
}
