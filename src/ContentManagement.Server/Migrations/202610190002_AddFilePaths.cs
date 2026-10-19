using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610190002_AddFilePaths")]
public sealed class AddFilePaths : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FilePath",
            table: "Files",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE [Files]
            SET [FilePath] = CONCAT(N'legacy/', LOWER(CONVERT(nvarchar(36), [Id])))
            WHERE [FilePath] IS NULL;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "FilePath",
            table: "Files",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(450)",
            oldMaxLength: 450,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Files_FilePath",
            table: "Files",
            column: "FilePath",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Files_FilePath", table: "Files");
        migrationBuilder.DropColumn(name: "FilePath", table: "Files");
    }
}
