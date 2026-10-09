using ContentManagement.Server.Auth;
using ContentManagement.Server.Compression;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

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

builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("ContentManagement");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddDbContext<ContentManagementDbContext>(options => options.UseSqlServer(connectionString));

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
    });

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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("otp-request", limiter =>
    {
        limiter.PermitLimit = 3;
        limiter.Window = TimeSpan.FromMinutes(10);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("otp-verify", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(10);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
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
app.MapControllers();
app.MapGet("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
