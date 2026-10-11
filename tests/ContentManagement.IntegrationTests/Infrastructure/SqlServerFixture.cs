using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using ContentManagement.Server.Data;
using Xunit;

namespace ContentManagement.IntegrationTests.Infrastructure;

/// <summary>
/// Spins up a SQL Server container once for the entire test collection,
/// runs EF Core migrations, and tears it down after all tests complete.
/// Shared via ICollectionFixture to avoid per-test startup overhead.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private async Task ApplyMigrationsAsync()
    {
        var options = new DbContextOptionsBuilder<ContentManagementDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        await using var db = new ContentManagementDbContext(options);
        await db.Database.MigrateAsync();
    }
}

[CollectionDefinition(Collections.Integration)]
public sealed class IntegrationCollection : ICollectionFixture<SqlServerFixture>
{
    // Marker class — xUnit wires up SqlServerFixture lifetime to this collection.
}

public static class Collections
{
    public const string Integration = "Integration";
}
