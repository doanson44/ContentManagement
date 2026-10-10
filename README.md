# ContentManagement

ContentManagement is a modular-monolith foundation for centralized content storage. The browser UI uses Blazor WebAssembly CSR; the ASP.NET Core server hosts the compiled client assets and HTTP API.

## Implemented foundation

- Exactly two application projects: `ContentManagement.Client` and `ContentManagement.Server`.
- Server-hosted Blazor WebAssembly static assets and client-side routing.
- EF Core SQL Server context and initial migration for binary metadata and compressed JSON records.
- GZIP JSON compressor validates JSON, enforces decompressed/uncompressed size limits, and calculates SHA-256 over the original UTF-8 JSON bytes.
- Filesystem storage streams bytes to a temporary file, enforces a size limit, calculates SHA-256, then atomically renames to a server-generated date-partitioned key.
- Lifecycle status enum and SQL row-version concurrency tokens.
- API-key authentication via the `X-Content-Management-Key` header and server-side scope policies for files/JSON read, write, and delete operations. All endpoints require authentication by default; health is explicitly anonymous.
- Unit tests for configuration, compression and filesystem storage; Docker SQL Server integration test applies migrations and verifies metadata persistence.
- GitHub Actions build/test pipeline and Windows x86 self-contained ZIP packaging.
- Initial file management API and Blazor WebAssembly page on the `feature/file-management-crud` branch: scoped list/upload/metadata/content/download/rename/soft-delete endpoints, pagination, image/PDF/video previews, bounded text preview, and browser HTTP Range support for media.

## Authentication configuration

Configure a high-entropy secret outside source control, for example through an environment variable or secret manager:

```powershell
$env:Authentication__ApiKey = "replace-with-a-long-random-secret"
$env:Authentication__Scopes__0 = "files.read"
$env:Authentication__Scopes__1 = "files.write"
$env:Authentication__Scopes__2 = "json.read"
```

Use a secret manager or protected environment variables in production; never embed this secret in Blazor WebAssembly configuration, JavaScript, appsettings committed to source control, or logs. The server fails at startup if no API key is configured. Send it on server-to-server calls as `X-Content-Management-Key`.

Supported scopes are `files.read`, `files.write`, `files.delete`, `json.read`, `json.write`, and `json.delete`. Grant only required scopes. Apply a policy such as `[Authorize(Policy = ScopePolicies.FilesRead)]` on each corresponding controller action. The fallback authorization policy requires authentication for all endpoints unless explicitly marked `[AllowAnonymous]`.

This static-key mode is a foundation for trusted server-to-server use. It currently uses one configured key/scope set; per-client API client registration, hashed client-secret storage, rotation/revocation, and OAuth 2.0 client credentials are not implemented. The browser administrator session is separate from this API-key mode. Do not use this API key as a browser login credential.

## Administrator email OTP login

The browser admin login uses email OTP and a server-issued cookie; it is separate from the static API-key mechanism used for server-to-server integrations. Configure exactly one administrator email in `AdminAuth:AllowedEmails`. Opening `/` redirects to `/otp`; if there is no active session, the client automatically requests an OTP to that configured mailbox, then asks only for the six-digit code. The client does not ask the administrator to enter an email address. There is no login form, public registration, invitation flow, or managed-user login.

Configure an explicit allowlist and a random HMAC key (at least 32 characters) through protected environment variables or a secret manager. Configure SMTP with the credentials for your email provider. Do not commit these values:

```powershell
$env:AdminAuth__AllowedEmails__0 = "admin@your-domain.example"
$env:AdminAuth__OtpHashKey = "<at-least-32-character-random-secret>"
$env:AdminAuth__PublicBaseUrl = "https://content.example.com"
# Optional only if the admin UI is intentionally hosted on another HTTPS origin:
$env:AdminAuth__AllowedOrigins__0 = "https://admin.your-domain.example"
$env:Smtp__Host = "smtp.your-provider.example"
$env:Smtp__Port = "587"
$env:Smtp__UseSsl = "true"
$env:Smtp__Username = "smtp-user"
$env:Smtp__Password = "<smtp-password>"
$env:Smtp__FromEmail = "noreply@your-domain.example"
$env:Authentication__ApiKey = "<server-to-server-api-key>"
```

The OTP challenge is persisted in SQL Server. OTPs expire, are single-use, are stored as keyed hashes, have a verification-attempt limit, and request/verification rate limits are partitioned by client IP. OTP issuance always targets the single email in `AdminAuth:AllowedEmails`, and OTP verification binds the code to that same server-configured address; the browser cannot choose an account. Unsafe requests without the API-key header must carry a same-origin `Origin` header or match an explicitly configured HTTPS origin in `AdminAuth:AllowedOrigins`; this is CSRF defense for cookie-authenticated operations. The API deliberately returns a generic message for eligible and ineligible email addresses. Admin sessions use an HttpOnly, Secure, SameSite=Strict cookie. HTTPS is required for the browser cookie. The SMTP sender must be configured before enabling any admin email in the allowlist.

The server applies pending ContentManagement database migrations automatically during startup before accepting requests. Back up the database before deploying a version that introduces schema changes, and review migration files before deployment. The application identity must have permission to apply schema changes; if migrations fail, startup fails and the exception is logged server-side.

The migrations include `202610090002_AddAdminOtpChallenges` and `202610090004_AddManagedUsers` (the latter is retained for database migration history; managed-user endpoints and sign-in have been removed). Configure the allowlist only for trusted administrators. The browser authentication surface provides administrator OTP sign-in and sign-out. A formal audit trail, email delivery observability, cross-instance distributed rate limiting, and synchronised OTP throttling across multiple server instances remain follow-up hardening tasks. The current origin check is a same-origin defense rather than a synchronizer-token implementation; keep browser and API on one origin where possible. API-key protected routes still require the relevant API-key scopes; the admin cookie does not grant those machine-to-machine scopes.

## Requirements

- .NET 10 SDK
- Docker with Docker Compose v2 for integration tests
- SQL Server container image access for integration tests

## Build and test

```bash
dotnet restore ContentManagement.sln
dotnet build ContentManagement.sln --configuration Release
dotnet test tests/ContentManagement.UnitTests/ContentManagement.UnitTests.csproj --configuration Release
```

Run integration tests against a disposable SQL Server container:

```bash
export MSSQL_SA_PASSWORD='Use-A-Strong-Local-Only-Password!'
docker compose -f Docker/docker-compose.integration.yml up -d --wait
export ConnectionStrings__IntegrationTests='Server=localhost,14333;Database=master;User Id=sa;Password=Use-A-Strong-Local-Only-Password!;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5'
dotnet test tests/ContentManagement.IntegrationTests/ContentManagement.IntegrationTests.csproj --configuration Release
docker compose -f Docker/docker-compose.integration.yml down --volumes
```

Use a local-only test password and do not commit credentials. CI uses a disposable SQL Server container and a CI-only password.

## Run the application

Set `Authentication__ApiKey` and then run:

```bash
dotnet run --project src/ContentManagement.Server/ContentManagement.Server.csproj
```

The server hosts the client at `/` and exposes the following anonymous health endpoints:

- `GET /api/health`: backward-compatible JSON response; returns HTTP 200 when SQL Server is reachable, or HTTP 503 when readiness fails.
- `GET /health/ready`: readiness probe. Checks connectivity through `ContentManagementDbContext`; returns HTTP 503 if SQL Server cannot be reached.
- `GET /health/live`: liveness probe. Checks only that the application process can serve requests and does not depend on SQL Server.

Health responses do not include connection strings, exception details, or other database diagnostics. Startup applies pending EF Core migrations before the server begins accepting requests; SQL Server must be reachable and the configured identity must have the required database and schema permissions. HTTPS redirection may require trusting the local development certificate.

## Database connection configuration

The server reads `ConnectionStrings:ContentManagement` and always registers the EF Core SQL Server context. The committed `appsettings.json` value is a local-development example for a SQL Server default instance using Windows Integrated Authentication:

```text
Server=localhost;Database=ContentManagement;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5
```

Ensure SQL Server is installed/running and the Windows identity running the application has permission to access the database. On startup, the server calls EF Core `Database.MigrateAsync()` to apply any pending migrations before serving requests. Review migrations and back up existing data before deploying schema changes. The SQL Server identity must have permission to create the database if it does not exist and to modify its schema; if the configured identity lacks these permissions or SQL Server is unavailable, startup fails.

Override the connection string per environment rather than committing credentials. For example, in PowerShell:

```powershell
$env:ConnectionStrings__ContentManagement = "Server=sql.example;Database=ContentManagement;User ID=contentmanagement_app;Password=<secret>;Encrypt=True;TrustServerCertificate=False"
```

Use a managed secret provider or protected environment variables for credentials, a least-privilege SQL login, and a trusted SQL Server TLS certificate in production. The local-development example sets `TrustServerCertificate=True`; do not carry that setting into production without a deliberate security review.

## Storage configuration

The `ContentManagement` section supports `StorageRoot` (filesystem root outside `wwwroot`), `MaxUploadBytes` (default 100 MiB), and `MaxJsonDocumentBytes` (default 10 MiB). Override through standard providers such as `ContentManagement__StorageRoot`, `ContentManagement__MaxUploadBytes`, and `ContentManagement__MaxJsonDocumentBytes`. Pending EF Core migrations are applied automatically at server startup.

## Data and storage notes

- Binary bytes are stored in date-partitioned directories under opaque server-generated keys; metadata is in SQL Server.
- JSON payloads are intended for GZIP-compressed UTF-8 storage in SQL Server `varbinary(max)`.
- SHA-256 is integrity metadata, not an authentication token.
- Filesystem writes and SQL metadata writes cannot share one atomic transaction. An application service must reconcile orphaned binaries and incomplete metadata writes.
- File endpoints require `files.read`, `files.write`, or `files.delete` scopes for API-key callers; the signed-in administrator cookie is allowed for the browser file-management UI. The current static API-key mode is shared across clients and does not provide per-resource ownership isolation. Add per-client identities, ownership policy, rate limiting, and audit logging before exposing the APIs to untrusted external clients.
- The inline content endpoint enables HTTP Range responses for browser media playback. Browser codec support varies. HTML and SVG are served as `application/octet-stream` to avoid active content executing in the application origin.
- Text preview is limited to files up to 256 KiB in the UI. Uploaded WebVTT subtitles are currently loaded locally into the browser for the active preview only; they are not persisted or associated with the video on the server. Persistent subtitle management is not yet implemented.

## Windows x86 package

CI publishes the server as a self-contained `win-x86` application, includes hosted WebAssembly assets, validates required output files, and uploads `ContentManagement-win-x86.zip` as a workflow artifact.

Before production use, configure external SQL Server connectivity, a persistent storage root with appropriate service-account permissions, HTTPS termination, backups, and production secrets. Because startup applies pending migrations automatically, back up the database and review migration changes before deploying a new version. This startup migration approach suits the intended single-server deployment; use a controlled migration bundle or reviewed SQL script if the deployment model changes to multiple application instances or requires separate schema-change approval.

## Operational services: SMTP, Serilog, and Hangfire

### SMTP email delivery

SMTP is already used by the administrator OTP flow. Configure `Smtp:Host`, `Smtp:Port`, `Smtp:UseSsl`, `Smtp:FromEmail`, and optionally `Smtp:FromName`; if the provider requires authentication, set both `Smtp:Username` and `Smtp:Password`. Set `AdminAuth:AllowedEmails` only after SMTP has been tested. SMTP values are read from `appsettings.json` and standard .NET configuration providers. Use environment variables or a secret manager for production credentials; do not commit real passwords.

### Serilog

The server uses Serilog for structured application and HTTP request logging. The committed defaults write to console and daily rolling files under `logs/contentmanagement-`. Fourteen files are retained by default. Ensure the Windows service/IIS application identity can write to the configured log directory, and provision disk monitoring/rotation appropriate to the deployment. Override sink settings through standard .NET configuration providers. Never log OTP values, API keys, SMTP credentials, document payloads, or file contents.

### Hangfire background jobs

Hangfire is enabled by default through `Hangfire:Enabled` in `appsettings.json`. Set `Hangfire__Enabled=false` only when intentionally disabling background processing.

Hangfire uses the same `ConnectionStrings:ContentManagement` SQL Server connection string as the application and stores its job/queue metadata in that database using Hangfire-managed tables. Ensure the configured SQL identity has the required permissions to create/update Hangfire tables as well as apply the application's EF Core migrations. This keeps deployment configuration simple, but the application and Hangfire share the same database availability and resource capacity. The worker runs in the server process. The dashboard is exposed at `/hangfire` while Hangfire is enabled and is restricted to a signed-in administrator session. Keep it behind HTTPS and do not expose it to the public internet without additional network controls. The stale-file cleanup job is registered by default. If Hangfire is explicitly disabled, cleanup does not run.

### Dashboard setup

After the server starts and automatically applies pending migrations, open `/dashboard/setup` as an administrator to configure stale-file cleanup policy. SMTP configuration is separate and is loaded from the server's `Smtp` section in `appsettings.json` or environment variables; the dashboard does not configure or test email delivery.

Default cleanup policy: files older than 90 days are eligible, the worker checks every 24 hours, and marked files wait 7 days before deletion. Hangfire polls hourly and runs when the configured interval has elapsed. The worker marks eligible files first and deletes the binary plus metadata only after the grace period. Each run is bounded to 500 marked files and 100 deletions. Cleanup runs only when Hangfire is enabled and configured.

Pending migrations are applied automatically during server startup. Before deploying new migrations, back up the database and review the migration files.

## Administrator dashboard

After a successful email OTP sign-in, the client navigates to `/dashboard`. The dashboard verifies the administrator session through `GET /api/auth/me` and redirects unauthenticated visitors to the sign-in page. It provides the workspace overview and navigation layout for API clients, files, and JSON documents. The Files module is available on the `feature/file-management-crud` branch and provides a paginated file list, upload, metadata display, rename, soft-delete, and browser-native previews for supported image/PDF/video types plus small text files. API clients and JSON management remain placeholders. The Setup page is dedicated to stale-file cleanup policy. SMTP is configured through server-side appsettings or environment variables.
