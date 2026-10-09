using Microsoft.Data.SqlClient;
namespace ContentManagement.IntegrationTests;
public sealed class SqlServerConnectivityTests
{
    [Fact]
    public async Task SqlServer_accepts_a_query()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__IntegrationTests");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "Set ConnectionStrings__IntegrationTests to the disposable Docker SQL Server connection string.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = await command.ExecuteScalarAsync();
        Assert.Equal(1, Convert.ToInt32(result));
    }
}
