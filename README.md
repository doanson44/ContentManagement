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

The browser admin login uses email OTP and a server-issued cookie; it is separate from the static API-key mechanism used for server-to-server integrations. The application has exactly one interactive identity: the administrator email address or addresses explicitly configured in `AdminAuth:AllowedEmails`. There is no public registration, invitation flow, or managed-user login.

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

The OTP challenge is persisted in SQL Server. OTPs expire, are single-use, are stored as keyed hashes, have a verification-attempt limit, and request/verification rate limits are partitioned by client IP. Both OTP issuance and verification enforce the server-side `AdminAuth:AllowedEmails` allowlist; an address outside that list cannot create a session. Unsafe requests without the API-key header must carry a same-origin `Origin` header or match an explicitly configured HTTPS origin in `AdminAuth:AllowedOrigins`; this is CSRF defense for cookie-authenticated operations. The API deliberately returns a generic message for eligible and ineligible email addresses. Admin sessions use an HttpOnly, Secure, SameSite=Strict cookie. HTTPS is required for the browser cookie. The SMTP sender must be configured before enabling any admin email in the allowlist.

Apply the new migration during a planned deployment after backing up the database:

```bash
dotnet ef database update --project src/ContentManagement.Server/ContentManagement.Server.csproj
```

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

The server hosts the client at `/` and the health endpoint at `/api/health`. HTTPS redirection may require trusting the local development certificate.

## Storage configuration

The `ContentManagement` section supports `StorageRoot` (filesystem root outside `wwwroot`), `MaxUploadBytes` (default 100 MiB), and `MaxJsonDocumentBytes` (default 10 MiB). Override through standard providers such as `ContentManagement__StorageRoot`, `ContentManagement__MaxUploadBytes`, and `ContentManagement__MaxJsonDocumentBytes`. Configure SQL with `ConnectionStrings__ContentManagement`. Migrations are not applied automatically; apply reviewed migrations during deployment after a backup.

## Data and storage notes

- Binary bytes are stored in date-partitioned directories under opaque server-generated keys; metadata is in SQL Server.
- JSON payloads are intended for GZIP-compressed UTF-8 storage in SQL Server `varbinary(max)`.
- SHA-256 is integrity metadata, not an authentication token.
- Filesystem writes and SQL metadata writes cannot share one atomic transaction. An application service must reconcile orphaned binaries and incomplete metadata writes.
- The storage services are not yet exposed through content APIs. Do not expose them until each endpoint has authentication, the appropriate scope policy, resource ownership checks, rate limits, and audit logging.

## Windows x86 package

CI publishes the server as a self-contained `win-x86` application, includes hosted WebAssembly assets, validates required output files, and uploads `ContentManagement-win-x86.zip` as a workflow artifact.

Before production use, configure external SQL Server connectivity, a persistent storage root with appropriate service-account permissions, HTTPS termination, backups, and production secrets. This baseline does not automatically apply database migrations.

## Operational services: SMTP, Serilog, and Hangfire

### SMTP email delivery

SMTP is already used by the administrator OTP flow. Configure `Smtp:Host`, `Smtp:Port`, `Smtp:UseSsl`, `Smtp:FromEmail`, and optionally `Smtp:FromName`; if the provider requires authentication, set both `Smtp:Username` and `Smtp:Password`. Set `AdminAuth:AllowedEmails` only after SMTP has been tested. SMTP values are read from `appsettings.json` and standard .NET configuration providers. Use environment variables or a secret manager for production credentials; do not commit real passwords.

### Serilog

The server uses Serilog for structured application and HTTP request logging. The committed defaults write to console and daily rolling files under `logs/contentmanagement-`. Fourteen files are retained by default. Ensure the Windows service/IIS application identity can write to the configured log directory, and provision disk monitoring/rotation appropriate to the deployment. Override sink settings through standard .NET configuration providers. Never log OTP values, API keys, SMTP credentials, document payloads, or file contents.

### Hangfire background jobs

Hangfire is optional and disabled by default. To enable it, configure a dedicated SQL Server connection string and turn on the feature flag:

```powershell
$env:Hangfire__Enabled = "true"
$env:ConnectionStrings__Hangfire = "Server=sql.example;Database=ContentManagementJobs;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=False"
```

Use a dedicated database/login with least-privilege access and a valid trusted SQL Server certificate in production. Hangfire creates/updates its schema when enabled; plan database permissions and deployment accordingly. The worker runs in the server process. The dashboard is exposed at `/hangfire` only when enabled and is restricted to a signed-in administrator session. Keep it behind HTTPS and do not expose it to the public internet without additional network controls. The stale-file cleanup job is registered when Hangfire is enabled. If Hangfire is disabled, cleanup does not run and the application does not require `ConnectionStrings:Hangfire`.


### Dashboard setup

After applying migration `202610090003_AddSystemSettings`, open `/dashboard/setup` as an administrator to configure stale-file cleanup policy. SMTP configuration is separate and is loaded from the server's `Smtp` section in `appsettings.json` or environment variables; the dashboard does not configure or test email delivery.

Default cleanup policy: files older than 90 days are eligible, the worker checks every 24 hours, and marked files wait 7 days before deletion. Hangfire polls hourly and runs when the configured interval has elapsed. The worker marks eligible files first and deletes the binary plus metadata only after the grace period. Each run is bounded to 500 marked files and 100 deletions. Cleanup runs only when Hangfire is enabled and configured.

Apply migrations after backing up the database:

```bash
dotnet ef database update --project src/ContentManagement.Server/ContentManagement.Server.csproj
```

## Administrator dashboard

After a successful email OTP sign-in, the client navigates to `/dashboard`. The dashboard verifies the administrator session through `GET /api/auth/me` and redirects unauthenticated visitors to the sign-in page. It currently provides the workspace overview and navigation layout for API clients, files, and JSON documents. The Setup page is dedicated to stale-file cleanup policy. SMTP is configured through server-side appsettings or environment variables. API client, file, and JSON management screens remain placeholders; their CRUD APIs have not yet been implemented.

