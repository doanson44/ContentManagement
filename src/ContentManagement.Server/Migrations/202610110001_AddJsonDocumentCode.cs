using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610110001_AddJsonDocumentCode")]
public sealed class AddJsonDocumentCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Code",
            table: "JsonDocuments",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: true);

        // Preserve existing rows while assigning a deterministic, unique code.
        migrationBuilder.Sql(
            "UPDATE [JsonDocuments] SET [Code] = N'json-' + REPLACE(CONVERT(nvarchar(36), [Id]), N'-', N'') WHERE [Code] IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "JsonDocuments",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(100)",
            oldMaxLength: 100,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_JsonDocuments_Code",
            table: "JsonDocuments",
            column: "Code",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_JsonDocuments_Code",
            table: "JsonDocuments");

        migrationBuilder.DropColumn(
            name: "Code",
            table: "JsonDocuments");
    }
}
