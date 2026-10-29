using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610290002_AddProxySwapSettings")]
public sealed class AddProxySwapSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ProxySwapEnabled",
            table: "SystemSettings",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "ProxySwapIntervalMinutes",
            table: "SystemSettings",
            type: "int",
            nullable: false,
            defaultValue: 60);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProxySwapEnabled", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ProxySwapIntervalMinutes", table: "SystemSettings");
    }
}