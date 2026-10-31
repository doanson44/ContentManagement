using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610310001_AddFileOwnership")]
public sealed class AddFileOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Type",
            table: "Files",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: nameof(FileOwnerType.Legacy));

        migrationBuilder.Sql("""
            UPDATE [Files]
            SET [Type] = N'User'
            WHERE [OwnerId] IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Type", table: "Files");
    }
}
