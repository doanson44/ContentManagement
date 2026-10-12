using System.Text.Json.Serialization;
using ContentManagement.FileApi.Auth;
using ContentManagement.FileApi.Configuration;
using ContentManagement.FileApi.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks();

builder.Services.AddOptions<FileApiOptions>()
    .Bind(builder.Configuration.GetSection(FileApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Authentication:ApiKey must be configured using a secret provider or environment variable.")
    .ValidateOnStart();

builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddAuthentication(StaticApiKeyDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, StaticApiKeyAuthenticationHandler>(
        StaticApiKeyDefaults.Scheme, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ContentManagement File API",
        Version = "v1",
        Description = "Standalone filesystem API secured by a static API key."
    });
    options.AddSecurityDefinition(StaticApiKeyDefaults.Scheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = StaticApiKeyDefaults.HeaderName,
        In = ParameterLocation.Header,
        Description = "Enter the static API key configured on the server."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(StaticApiKeyDefaults.Scheme, document)] = []
    });
});

var builderMaxRequestBodyBytes = 104_857_600L + 65_536L;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = builderMaxRequestBodyBytes);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions())
    .AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program;
