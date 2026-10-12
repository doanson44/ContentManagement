using ContentManagement.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentManagement.Server.Data.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
[Migration("202610120001_EnumValuesAsStrings")]
public sealed class EnumValuesAsStrings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX [IX_Files_Status_CreatedUtc] ON [Files];
            DROP INDEX [IX_JsonDocuments_Status_UpdatedUtc] ON [JsonDocuments];

            ALTER TABLE [Files] ALTER COLUMN [Status] nvarchar(32) NOT NULL;
            ALTER TABLE [JsonDocuments] ALTER COLUMN [Status] nvarchar(32) NOT NULL;

            UPDATE [Files]
            SET [Status] = CASE [Status]
                WHEN N'0' THEN N'Uploading'
                WHEN N'1' THEN N'Active'
                WHEN N'2' THEN N'MarkedForDeletion'
                WHEN N'3' THEN N'Deleting'
                WHEN N'4' THEN N'Deleted'
                ELSE [Status]
            END;

            UPDATE [JsonDocuments]
            SET [Status] = CASE [Status]
                WHEN N'0' THEN N'Uploading'
                WHEN N'1' THEN N'Active'
                WHEN N'2' THEN N'MarkedForDeletion'
                WHEN N'3' THEN N'Deleting'
                WHEN N'4' THEN N'Deleted'
                ELSE [Status]
            END;

            CREATE INDEX [IX_Files_Status_CreatedUtc] ON [Files] ([Status], [CreatedUtc]);
            CREATE INDEX [IX_JsonDocuments_Status_UpdatedUtc] ON [JsonDocuments] ([Status], [UpdatedUtc]);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX [IX_Files_Status_CreatedUtc] ON [Files];
            DROP INDEX [IX_JsonDocuments_Status_UpdatedUtc] ON [JsonDocuments];

            UPDATE [Files]
            SET [Status] = CASE [Status]
                WHEN N'Uploading' THEN N'0'
                WHEN N'Active' THEN N'1'
                WHEN N'MarkedForDeletion' THEN N'2'
                WHEN N'Deleting' THEN N'3'
                WHEN N'Deleted' THEN N'4'
                ELSE [Status]
            END;

            UPDATE [JsonDocuments]
            SET [Status] = CASE [Status]
                WHEN N'Uploading' THEN N'0'
                WHEN N'Active' THEN N'1'
                WHEN N'MarkedForDeletion' THEN N'2'
                WHEN N'Deleting' THEN N'3'
                WHEN N'Deleted' THEN N'4'
                ELSE [Status]
            END;

            ALTER TABLE [Files] ALTER COLUMN [Status] int NOT NULL;
            ALTER TABLE [JsonDocuments] ALTER COLUMN [Status] int NOT NULL;

            CREATE INDEX [IX_Files_Status_CreatedUtc] ON [Files] ([Status], [CreatedUtc]);
            CREATE INDEX [IX_JsonDocuments_Status_UpdatedUtc] ON [JsonDocuments] ([Status], [UpdatedUtc]);
            """);
    }
}
