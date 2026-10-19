using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610120002_AddFileUploadSource")]
public sealed class AddFileUploadSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UploadSource",
            table: "Files",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: nameof(FileUploadSource.Local));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "UploadSource",
            table: "Files");
    }
}
