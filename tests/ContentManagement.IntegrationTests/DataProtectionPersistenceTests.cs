using ContentManagement.IntegrationTests.Infrastructure;
using ContentManagement.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class DataProtectionPersistenceTests(SqlServerFixture fixture)
{
    [Fact]
    public void KeysPersistAcrossServiceProviderInstances()
    {
        const string plaintext = "content-management-data-protection-roundtrip";
        string protectedPayload;

        using (var firstProvider = CreateProvider())
        {
            var protector = firstProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("ContentManagement.IntegrationTests");
            protectedPayload = protector.Protect(plaintext);
        }

        using (var secondProvider = CreateProvider())
        {
            var protector = secondProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("ContentManagement.IntegrationTests");

            Assert.Equal(plaintext, protector.Unprotect(protectedPayload));
        }
    }

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ContentManagementDbContext>(options =>
            options.UseSqlServer(fixture.ConnectionString));
        services.AddDataProtection()
            .SetApplicationName("ContentManagement")
            .PersistKeysToDbContext<ContentManagementDbContext>();

        return services.BuildServiceProvider();
    }
}
