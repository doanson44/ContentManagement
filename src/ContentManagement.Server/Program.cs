using ContentManagement.Server.Auth;
using ContentManagement.Server.Compression;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();

builder.Services.AddOptions<ContentManagementOptions>()
    .Bind(builder.Configuration.GetSection(ContentManagementOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Authentication:ApiKey must be configured using a secret provider.")
    .Validate(options => options.Scopes.All(scope =>
        scope is ScopePolicies.FilesRead or ScopePolicies.FilesWrite or ScopePolicies.FilesDelete or
            ScopePolicies.JsonRead or ScopePolicies.JsonWrite or ScopePolicies.JsonDelete),
        "Authentication:Scopes contains an unsupported scope.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("ContentManagement");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddDbContext<ContentManagementDbContext>(options => options.UseSqlServer(connectionString));

builder.Services.AddSingleton<IContentCompressor, GzipContentCompressor>();
builder.Services.AddSingleton<IFileStorage, FileSystemStorage>();

builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.Scheme, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    foreach (var scope in new[]
    {
        ScopePolicies.FilesRead, ScopePolicies.FilesWrite, ScopePolicies.FilesDelete,
        ScopePolicies.JsonRead, ScopePolicies.JsonWrite, ScopePolicies.JsonDelete
    })
    {
        options.AddPolicy(scope, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", scope));
    }
});

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
