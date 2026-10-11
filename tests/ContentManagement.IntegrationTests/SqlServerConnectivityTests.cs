using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class SqlServerConnectivityTests(SqlServerFixture db)
{
    [Fact]
    public async Task Migrations_create_schema_and_persist_content_metadata()
    {
        // Arrange — container and migrations are already applied by the fixture.
        var options = new DbContextOptionsBuilder<ContentManagementDbContext>()
            .UseSqlServer(db.ConnectionString)
            .Options;

        await using var context = new ContentManagementDbContext(options);

        var record = new StoredFile
        {
            Id = Guid.NewGuid(),
            FileName = "integration.txt",
            ContentType = "text/plain",
            StorageKey = $"2026/10/09/{Guid.NewGuid():N}.bin",
            SizeBytes = 12,
            Sha256 = new string('A', 64),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Status = ContentStatus.Active
        };

        // Act
        context.Files.Add(record);
        await context.SaveChangesAsync();

        // Assert
        var persisted = await context.Files
            .AsNoTracking()
            .SingleAsync(f => f.Id == record.Id);

        Assert.Equal(record.StorageKey, persisted.StorageKey);
        Assert.Equal(record.SizeBytes, persisted.SizeBytes);
        Assert.Equal(ContentStatus.Active, persisted.Status);
    }
}
