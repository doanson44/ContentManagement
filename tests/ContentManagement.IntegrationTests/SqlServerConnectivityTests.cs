using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContentManagement.IntegrationTests;

public sealed class SqlServerConnectivityTests
{
    [Fact]
    public async Task Migrations_create_schema_and_persist_content_metadata()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__IntegrationTests");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "Set ConnectionStrings__IntegrationTests to the disposable Docker SQL Server connection string.");

        var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using (var master = new SqlConnection(masterBuilder.ConnectionString))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandText = "IF DB_ID(N'ContentManagementIntegration') IS NULL CREATE DATABASE [ContentManagementIntegration];";
            await command.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "ContentManagementIntegration" };
        var options = new DbContextOptionsBuilder<ContentManagementDbContext>().UseSqlServer(databaseBuilder.ConnectionString).Options;
        await using var db = new ContentManagementDbContext(options);
        await db.Database.MigrateAsync();

        var record = new StoredFile
        {
            Id = Guid.NewGuid(), FileName = "integration.txt", ContentType = "text/plain",
            StorageKey = $"2026/10/09/{Guid.NewGuid():N}.bin", SizeBytes = 12,
            Sha256 = new string('A', 64), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            Status = ContentStatus.Active
        };
        db.Files.Add(record);
        await db.SaveChangesAsync();
        var persisted = await db.Files.AsNoTracking().SingleAsync(file => file.Id == record.Id);
        Assert.Equal(record.StorageKey, persisted.StorageKey);
        Assert.Equal(record.SizeBytes, persisted.SizeBytes);
        Assert.Equal(ContentStatus.Active, persisted.Status);
    }
}
