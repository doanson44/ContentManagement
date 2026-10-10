using ContentManagement.Server.Auth;
using ContentManagement.Server.BackgroundJobs;
using ContentManagement.Server.Compression;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Storage;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "ContentManagement.Server"));

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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

builder.Services.AddOptions<AdminAuthOptions>()
    .Bind(builder.Configuration.GetSection(AdminAuthOptions.SectionName))
    .Validate(options => options.OtpLifetimeMinutes is >= 3 and <= 30)
    .Validate(options => options.MaxVerificationAttempts is >= 3 and <= 10)
    .Validate(options => options.ResendCooldownSeconds is >= 30 and <= 600)
    .Validate(options => options.SessionLifetimeHours is >= 1 and <= 24)
    .Validate(options => options.AllowedOrigins.All(origin => Uri.TryCreate(origin, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps),
        "AdminAuth:AllowedOrigins must contain absolute HTTPS origins.")
    .Validate(options => options.AllowedEmails.Length == 0 || options.OtpHashKey.Length >= 32,
        "AdminAuth:OtpHashKey must contain at least 32 characters when admin email sign-in is enabled.")
    .ValidateOnStart();

var allowedAdminEmails = builder.Configuration.GetSection("AdminAuth:AllowedEmails").Get<string[]>() ?? [];
builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
    .Validate(options => options.Port is >= 1 and <= 65535, "Smtp:Port must be a valid TCP port.")
    .Validate(options => options.Port is >= 1 and <= 65535, "Smtp:Port must be a valid TCP port.")
    .Validate(options => string.IsNullOrWhiteSpace(options.Username) || !string.IsNullOrWhiteSpace(options.Password),
        "Smtp:Password is required when Smtp:Username is configured.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("ContentManagement");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:ContentManagement must be configured.");

builder.Services.AddDbContext<ContentManagementDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ContentManagementDbContext>("sqlserver", tags: ["ready"]);

var hangfireEnabled = builder.Configuration.GetValue<bool>("Hangfire:Enabled");
if (hangfireEnabled)
{
    builder.Services.AddScoped<StaleFileCleanupJob>();
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(15),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));
    builder.Services.AddHangfireServer();
}

builder.Services.AddSingleton<IContentCompressor, GzipContentCompressor>();
builder.Services.AddSingleton<IFileStorage, FileSystemStorage>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "ContentManagement";
        options.DefaultAuthenticateScheme = "ContentManagement";
        options.DefaultChallengeScheme = "ContentManagement";
    })
    .AddPolicyScheme("ContentManagement", "API key or admin cookie", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey(ApiKeyAuthenticationDefaults.HeaderName)
                ? ApiKeyAuthenticationDefaults.Scheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.Scheme, _ => { })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "__Host-ContentManagement.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var principal = context.Principal;
            var adminEmail = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            if (principal?.IsInRole("Administrator") != true ||
                string.IsNullOrWhiteSpace(adminEmail) ||
                !allowedAdminEmails.Contains(adminEmail, StringComparer.OrdinalIgnoreCase))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Browser administrator endpoints use the HttpOnly admin cookie; machine-to-machine
    // callers must still present an API key carrying the exact scope.
    foreach (var scope in new[] { ScopePolicies.FilesRead, ScopePolicies.FilesWrite, ScopePolicies.FilesDelete })
    {
        options.AddPolicy(scope, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.IsInRole("Administrator") ||
                context.User.Claims.Any(claim => claim.Type == "scope" && claim.Value == scope)));
    }

    foreach (var scope in new[] { ScopePolicies.JsonRead, ScopePolicies.JsonWrite, ScopePolicies.JsonDelete })
    {
        options.AddPolicy(scope, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", scope));
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("otp-request", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("otp-verify", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

try
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<ContentManagementDbContext>();

    app.Logger.LogInformation("Applying pending ContentManagement database migrations.");
    await dbContext.Database.MigrateAsync();
    app.Logger.LogInformation("ContentManagement database migrations completed.");
}
catch (Exception exception)
{
    app.Logger.LogCritical(exception,
        "Database migration failed during startup. The application will not start.");
    throw;
}

app.UseSerilogRequestLogging();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var isUnsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    var hasApiKey = context.Request.Headers.ContainsKey(ApiKeyAuthenticationDefaults.HeaderName);
    if (isUnsafeMethod && !hasApiKey)
    {
        var originHeader = context.Request.Headers.Origin.ToString();
        var allowedOrigins = builder.Configuration.GetSection("AdminAuth:AllowedOrigins").Get<string[]>() ?? [];
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var originIsAllowed = Uri.TryCreate(originHeader, UriKind.Absolute, out var origin) &&
            (string.Equals(origin.GetLeftPart(UriPartial.Authority), requestOrigin, StringComparison.OrdinalIgnoreCase) ||
             allowedOrigins.Contains(origin.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase));
        if (!originIsAllowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "Request origin is not allowed." });
            return;
        }
    }
    await next();
});
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (hangfireEnabled)
{
    RecurringJob.AddOrUpdate<StaleFileCleanupJob>(
        "stale-file-cleanup", job => job.ExecuteAsync(CancellationToken.None), Cron.Hourly);
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new AdminDashboardAuthorizationFilter()]
    });
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.MapControllers();
app.MapGet("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
