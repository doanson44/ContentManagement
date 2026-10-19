# ContentManagement

ContentManagement is a modular-monolith foundation for centralized content storage. The browser UI uses Blazor WebAssembly CSR; the ASP.NET Core server hosts the compiled client assets and HTTP API.

## Implemented foundation

- The hosted web application consists of `ContentManagement.Client` and `ContentManagement.Server`.
- `ContentManagement.FileApi` is a separate, standalone filesystem API process. It does not reference the web server project; its readiness health check verifies the configured MySQL dependency.
- Server-hosted Blazor WebAssembly static assets and client-side routing.
- EF Core SQL Server context and initial migration for binary metadata and compressed JSON records.
- GZIP JSON compressor validates JSON, enforces decompressed/uncompressed size limits, and calculates SHA-256 over the original UTF-8 JSON bytes.
- Filesystem storage streams bytes to a temporary file, enforces a size limit, calculates SHA-256, then atomically renames to a server-generated date-partitioned key.
- Lifecycle status enum and SQL row-version concurrency tokens.
- File metadata records `UploadSource` (`Local` or `ExternalServer`); uploads authenticated with a managed API key are classified as external-server uploads, while browser/admin uploads are classified as local. Existing rows default to `Local` through an additive migration.
- All EF Core enum properties are stored in SQL Server as their symbolic string names; the `EnumValuesAsStrings` migration converts existing numeric file/document statuses.
- Managed API-client authentication via the `X-Content-Management-Key` header and server-side scope policies for files/JSON read, write, and delete operations. Browser administrators use the admin session cookie. All endpoints require authentication by default; health and signed file-view URLs are explicitly anonymous.
- Unit tests for configuration, compression and filesystem storage; Docker SQL Server integration test applies migrations and verifies metadata persistence.
- GitHub Actions build/test pipeline and Windows x86 self-contained ZIP packaging.
- Initial file management API and Blazor WebAssembly page on the `feature/file-management-crud` branch: scoped list/upload/metadata/content/download/rename/soft-delete endpoints, pagination, image/PDF/video previews, bounded text preview, and browser HTTP Range support for media.


## Orphan binary cleanup

When Hangfire is enabled, the server runs the `orphan-file-cleanup` job daily at 01:00 in the server's local time zone. It reconciles local filesystem `.bin` files and External File API `.bin` files against SQL metadata and deletes binaries that have no corresponding file record for that provider.

The job uses a 24-hour grace period to avoid racing with an upload whose binary has been written but whose metadata has not yet committed. It skips reparse-point/symbolic-link entries, deletes at most 500 orphan candidates per provider per run, logs failures, and retries remaining candidates on a later daily run. External inventory is exposed only through the authenticated File API. Configure `Hangfire:Enabled=true` for the recurring job to be registered and executed. The cleanup is intentionally limited to `.bin` files; it does not remove preview cache files or arbitrary files under the storage root.

## Standalone File API

`ContentManagement.FileApi` is an independent ASP.NET Core API with no database dependency. It stores files under `FileApi:RootPath` and accepts only relative `filePath` values; absolute paths, traversal segments, invalid segments, and symlink/reparse-point paths are rejected. Uploads never overwrite an existing path. The default upload limit is 100 MiB and is enforced while copying to a temporary file; the file is atomically moved into place after the upload succeeds. The API returns the file size and SHA-256 digest.

Endpoints:

- `GET /health` — anonymous health check.
- `PUT /api/files?filePath=relative/path.ext` — multipart upload with a form field named `file` and a relative `filePath` query parameter.
- `GET /api/files/download?filePath=relative/path.ext` — download as an attachment.
- `GET /api/files/view?filePath=relative/path.ext` — inline view for safe image, PDF, audio, video, plain-text, and CSV types. HTML, SVG, and unknown file types are not rendered inline.
- `GET /api/files/inventory?olderThanUtc=<UTC timestamp>` — authenticated inventory of old `.bin` files for server-side orphan reconciliation.
- `GET /swagger` — Swagger UI with an `Authorize` button for the static API key.

Configure the storage root and secret through environment variables (PowerShell example):

```powershell
$env:FileApi__RootPath = "D:\\ContentManagement\\files"
$env:FileApi__MaxUploadBytes = "104857600"
$env:Authentication__ApiKey = "<generate-a-random-secret-at-least-32-characters-long>"
dotnet run --project src/ContentManagement.FileApi/ContentManagement.FileApi.csproj
```

The secret is intentionally blank in committed `appsettings.json`; the API fails at startup if `Authentication:ApiKey` is missing or shorter than 32 characters. Swagger sends the key in the `X-API-Key` header. Keep the API behind HTTPS, restrict network access to trusted clients, and grant the service account access only to the configured storage directory. Configure `FileApi:RootPath` to a dedicated directory, not a directory containing application files or secrets.

## WMV browser previews

Browsers generally do not decode WMV/Windows Media codecs natively. File management therefore keeps the original WMV binary unchanged and serves a cached H.264/AAC MP4 rendition from the authenticated `GET /api/files/{id}/preview` endpoint. Other video types continue to stream their original file, and downloads always return the original binary.

Install a trusted FFmpeg build on the server and configure `ContentManagement:FfmpegPath` to its absolute executable path (for example, `C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe` on Windows). If omitted, the server searches for `ffmpeg` on the process PATH. Example environment configuration:

```powershell
$env:ContentManagement__FfmpegPath = "C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe"
# Optional: keep generated previews outside the binary storage directory.
$env:ContentManagement__PreviewCacheRoot = "D:\\ContentManagement\\preview-cache"
$env:ContentManagement__MaxPreviewCacheBytes = "2147483648"
```

Preview generation is lazy and cached by the original file's SHA-256, serialized to one conversion at a time, limited to two minutes per conversion, and uses at most two FFmpeg encoder threads. The default preview cache limit is 2 GiB; old cached MP4s are pruned when new previews are generated. Ensure the service account can execute FFmpeg and read/write the configured storage/cache directories. FFmpeg must be available in the deployment environment separately; it is not bundled into the application ZIP. A source with unsupported codecs or a conversion that exceeds the timeout will return a preview error while leaving the original file downloadable.

## JSON document codes

Each JSON document has a unique `Code` in addition to its internal GUID. Deleting a JSON document through `DELETE /api/json/{id}` permanently removes its row and payload from SQL Server immediately; JSON documents do not use the soft-delete/grace-period workflow. The admin editor lets you enter a code when creating or editing a document. Leave the code blank on create to have the server generate a value in the form `json-<guid>`. Codes are trimmed, limited to 100 characters, and may contain letters, numbers, dots, underscores, and hyphens; the first character must be a letter or number. Duplicate codes return HTTP 409. Existing records receive stable generated codes through the `AddJsonDocumentCode` migration. The JSON list search matches code, document type, SHA-256, and GUID.

For API creation, send an optional `code` field with `documentType` and `json`. Omitting or sending a blank code generates one. For updates, omitting/blanking `code` preserves the existing code. Retrieve a document directly by its code with authenticated `GET /api/json/by-code/{code}`; the response uses the same document details schema as `GET /api/json/{id}`. External systems that do not know the internal GUID can create or update a document idempotently with `PUT /api/json/by-code/{code}` and a body containing `documentType` and `json`. The upsert requires `json.write`, creates the document and returns `201 Created` when the code is new, or updates the existing active document and returns `200 OK`. The code in the URL is authoritative and remains stable; the endpoint will not overwrite records that are in a non-active lifecycle state. Concurrent upserts for the same code are serialized using SQL Server transaction isolation and the unique code index.

## Authentication configuration

Configure a high-entropy secret outside source control, for example through an environment variable or secret manager:

```powershell
# Create a managed API client from /dashboard/api-clients and store its key in the consuming server application.
```

Use a secret manager or protected environment variables in production; never embed client credentials in Blazor WebAssembly configuration, JavaScript, appsettings committed to source control, or logs. Create managed API clients from `/dashboard/api-clients` and send each client's key on server-to-server calls as `X-Content-Management-Key`. There is no global static API key requirement.

Supported scopes are `files.read`, `files.write`, `files.delete`, `json.read`, `json.write`, and `json.delete`. Grant only required scopes. Apply a policy such as `[Authorize(Policy = ScopePolicies.FilesRead)]` on each corresponding controller action. The fallback authorization policy requires authentication for all endpoints unless explicitly marked `[AllowAnonymous]`.

Managed API clients can be created from /dashboard/api-clients; each receives a server-generated client ID and high-entropy secret. The database stores only the SHA-256 hash of the secret. The full key is shown only on creation or rotation and is sent in the X-Content-Management-Key header in the form cm_<client-id>.<secret>. Each managed client has its own file/JSON scopes. Disabled or deleted clients are rejected on subsequent requests. The browser administrator session is separate from API client credentials. This feature uses managed opaque API keys, not OAuth 2.0 Client Credentials; OAuth token issuance remains a future enhancement.

## Administrator email OTP login

The browser admin login uses email OTP and a server-issued cookie; it is separate from managed API-client credentials used for server-to-server integrations. Configure exactly one administrator email in `AdminAuth:AllowedEmails`. Opening `/` redirects to `/otp`; if there is no active session, the client automatically requests an OTP to that configured mailbox, then asks only for the six-digit code. The client does not ask the administrator to enter an email address. There is no login form, public registration, invitation flow, or managed-user login.

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
```

The OTP challenge is persisted in SQL Server. OTPs expire, are single-use, are stored as keyed hashes, have a verification-attempt limit, and request/verification rate limits are partitioned by client IP. OTP issuance always targets the single email in `AdminAuth:AllowedEmails`, and OTP verification binds the code to that same server-configured address; the browser cannot choose an account. Unsafe requests without the managed API-client header must carry a same-origin `Origin` header or match an explicitly configured HTTPS origin in `AdminAuth:AllowedOrigins`; this is CSRF defense for cookie-authenticated operations. The API deliberately returns a generic message for eligible and ineligible email addresses. Admin sessions use an HttpOnly, Secure, SameSite=Strict cookie. HTTPS is required for the browser cookie. The SMTP sender must be configured before enabling any admin email in the allowlist.

The server applies pending ContentManagement database migrations automatically during startup before accepting requests. Back up the database before deploying a version that introduces schema changes, and review migration files before deployment. The application identity must have permission to apply schema changes; if migrations fail, startup fails and the exception is logged server-side.

The migrations include `202610090002_AddAdminOtpChallenges` and `202610090004_AddManagedUsers` (the latter is retained for database migration history; managed-user endpoints and sign-in have been removed). Configure the allowlist only for trusted administrators. The browser authentication surface provides administrator OTP sign-in and sign-out. A formal audit trail, email delivery observability, cross-instance distributed rate limiting, and synchronised OTP throttling across multiple server instances remain follow-up hardening tasks. The current origin check is a same-origin defense rather than a synchronizer-token implementation; keep browser and API on one origin where possible. Managed API-client routes require the relevant client scopes; the admin cookie does not grant those machine-to-machine scopes.

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

## Swagger / OpenAPI

In the Development environment, the server exposes the interactive Swagger UI at `/swagger` and the OpenAPI JSON at `/swagger/v1/swagger.json`. Swagger is disabled outside Development by default to avoid publishing the API surface in production. The document describes both supported authentication alternatives for protected operations: the `X-Content-Management-Key` header for managed API clients and the administrator session cookie. Client calls still require the relevant file/JSON scope; never enter production secrets on an untrusted machine or embed keys in the Blazor WebAssembly client.

## Run the application

Create a managed API client in the administrator dashboard only if a server-to-server integration needs API access, then run:

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

ASP.NET Core Data Protection keys are persisted in the `DataProtectionKeys` table in this same SQL Server database, so the application does not need filesystem access for its key ring. Treat this table as cryptographic secret material: restrict database permissions, use the hosting provider's database encryption-at-rest controls where available, and include the table in backups. Do not delete its rows; losing the key ring invalidates existing protected cookies and other Data Protection payloads.

Override the connection string per environment rather than committing credentials. For example, in PowerShell:

```powershell
$env:ConnectionStrings__ContentManagement = "Server=sql.example;Database=ContentManagement;User ID=contentmanagement_app;Password=<secret>;Encrypt=True;TrustServerCertificate=False"
```

Use a managed secret provider or protected environment variables for credentials, a least-privilege SQL login, and a trusted SQL Server TLS certificate in production. The local-development example sets `TrustServerCertificate=True`; do not carry that setting into production without a deliberate security review.

## Storage configuration

The `ContentManagement` section supports `StorageRoot` (filesystem root outside `wwwroot`) and `MaxUploadBytes` (default 100 MiB). JSON documents have no application-configured payload-size limit; Kestrel request-body limiting is disabled. The practical maximum is still bounded by available memory, .NET string/array limits, SQL Server `varbinary(max)` (approximately 2 GB per value), and any IIS/reverse-proxy request limits in front of the app. Size limits should be set at the hosting boundary only when intentionally required to protect capacity. Pending EF Core migrations are applied automatically at server startup.

## Data and storage notes

- Binary bytes are stored in date-partitioned directories under opaque server-generated keys; metadata is in SQL Server, including the upload source (`Local` or `ExternalServer`).
- JSON payloads are intended for GZIP-compressed UTF-8 storage in SQL Server `varbinary(max)`.
- SHA-256 is integrity metadata, not an authentication token.
- Filesystem writes and SQL metadata writes cannot share one atomic transaction. An application service must reconcile orphaned binaries and incomplete metadata writes.
- File endpoints require `files.read`, `files.write`, or `files.delete` scopes for managed API-client callers; the signed-in administrator cookie is allowed for the browser file-management UI. Add per-client identities, ownership policy, rate limiting, and audit logging before exposing the APIs to untrusted external clients.
- The inline content endpoint enables HTTP Range responses for browser media playback. Browser codec support varies. HTML and SVG are served as `application/octet-stream` to avoid active content executing in the application origin.
- Text preview is limited to files up to 256 KiB in the UI. Uploaded WebVTT subtitles are currently loaded locally into the browser for the active preview only; they are not persisted or associated with the video on the server. Persistent subtitle management is not yet implemented.

## Windows x86 package

CI publishes the server as a self-contained `win-x86` application, includes hosted WebAssembly assets, validates required output files, and uploads `ContentManagement-win-x86.zip` as a workflow artifact.

Run `publish-deploy.bat` from the repository root to build the same deployment packages locally. It runs the unit tests, publishes `ContentManagement.Server` (with the hosted Blazor WebAssembly client) and `ContentManagement.FileApi` as self-contained `win-x86` builds, validates the publish output, and writes `artifacts\ContentManagement-win-x86.zip` and `artifacts\ContentManagement-FileApi-win-x86.zip`. Nothing is packaged when a step fails; pass `-SkipTests` to package without running the unit test gate. Gitignored local settings files (`appsettings.Development.json`, `appsettings.Local.json`) are deleted from the publish output before packaging so local secrets are not shipped; supply production configuration through environment variables on the target server. Copy the ZIPs to the target server and extract them there; the File API is a separate process with its own storage root and API key configuration.

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

After a successful email OTP sign-in, the client navigates to `/dashboard`. The dashboard verifies the administrator session through `GET /api/auth/me` and redirects unauthenticated visitors to the sign-in page. It provides the workspace overview and navigation layout for API clients, files, and JSON documents. The Files module provides a paginated file list, upload, metadata display, rename, soft-delete, and browser-native previews for supported image/PDF/video types plus small text files. JSON management supports listing, searching, validation, create, update, and permanent delete. API client management supports create/list/update/disable/secret rotation/delete and per-client file/JSON scopes. The Setup page controls stale-file cleanup policy and external storage enablement/quota. External storage endpoint and credentials, like SMTP settings, are configured through server-side appsettings or environment variables.


## Managed API client administration

Administrators can use /dashboard/api-clients to create, list, edit, disable, rotate, and delete API clients. Available scopes are files.read, files.write, files.delete, json.read, json.write, and json.delete. Assign only the scopes required by each integration.

A new client receives an API key in the form cm_<client-id>.<secret>. Store the complete key in the consuming application's server-side secret store; it cannot be retrieved from ContentManagement after the create/rotate response. The database stores a SHA-256 hash of the randomly generated 256-bit secret, not the plaintext secret. Rotating the secret invalidates the previous key immediately. Disabling or deleting a client also prevents future authentication.

Managed API clients are authenticated through the existing X-Content-Management-Key header. Managed API client scopes are enforced by the same file and JSON authorization policies; no global static API key is required. Client management endpoints require the administrator cookie and are not available to managed API clients.

This feature does not implement OAuth 2.0 Client Credentials or resource-level ownership isolation. Scope assignment is currently at the API capability level (files and JSON); do not expose the API to untrusted clients without separately implementing and reviewing resource-level authorization, API rate limits, and audit logging.

## MySQL health check

The File API `GET /health` endpoint checks connectivity to the configured MySQL database by opening a connection and running `SELECT 1`. The check has an 8-second overall health-check timeout, uses a 5-second connection/query timeout, and reports unhealthy if the connection string is missing, the database cannot be reached, authentication fails, or the probe query fails. It does not read or modify application data.

Configure `ConnectionStrings:MySql` in `src/ContentManagement.FileApi/appsettings.json` for local development. For production, override it with the `ConnectionStrings__MySql` environment variable or a secret provider so credentials are not committed to source control. Example connection string format: `Server=<host>;Port=3306;Database=<database>;User ID=<username>;Password=<secret>;`. The File API's file bytes remain on the filesystem; MySQL is checked as a dependency and is not used for file storage.

## Quick Notes

Quick Notes is an administrator-only personal note area available at `/dashboard/quick-notes`. It stores plain-text titles and content in SQL Server, independently of file and JSON content. The dashboard includes a Quick Notes entry for the signed-in administrator.

- Notes can be created, searched by title/content, pinned, edited, and deleted.
- Title length is limited to 200 characters; note content is limited to 20,000 characters.
- Edits are sent after a 650 ms debounce. The UI reports pending, saving, saved, and failed states; failed edits remain in the editor.
- Updates use SQL Server row-version concurrency checks. A stale update returns HTTP 409 rather than silently overwriting a concurrent change.
- The API is under `/api/admin/quick-notes` and requires the administrator cookie and `Administrator` role. It is not available to API-key clients.
- Migration `202610120001_AddQuickNotes` creates the notes table and its ordering index. The server applies pending EF Core migrations at startup, so back up the database and review the migration before deployment.

Quick Notes currently supports the application's configured administrator session, not multiple independent user accounts. Content is plain text; rich text, attachments, sharing, reminders, and note version history are not implemented.

### External File API storage

Configure the standalone ContentManagement.FileApi connection on the server, not through the browser UI:

- Set `ExternalStorage:BaseUrl` and `ExternalStorage:ApiKey` in `src/ContentManagement.Server/appsettings.json`, an environment-specific appsettings file, or server environment variables. Keep production credentials out of source control; environment variables or a secret store are preferred for deployment secrets.
- The administrator setup page only controls whether external storage is enabled and its quota in GiB. It displays whether the server-side connection is configured but never reads or returns the API key.
- When external storage is enabled and a file fits within the remaining configured quota, the main server streams it to `PUT /api/files?filePath=...` using the `X-API-Key` header, then stores metadata in SQL Server with `UploadSource = ExternalServer`.
- Quota usage is calculated from non-deleted Files metadata records with `UploadSource = ExternalServer`; remaining capacity is `max(0, quota - used)`. No capacity synchronization with the standalone API is performed.
- If the file would exceed the configured quota, it is stored on the main server's local filesystem. If an external upload fails, the request fails instead of silently falling back to local storage.
- Downloads, inline content reads, and stale-file cleanup use the storage source recorded for each file. Since the external endpoint is application-wide configuration, changing it while external file records exist may make existing binaries unavailable; update it only when the old endpoint's files have been migrated.
- The main application serializes external quota checks/uploads within its single process. This assumes the documented single-server deployment; multi-instance deployments require a distributed quota reservation mechanism.

The External API must be deployed separately and reachable from the main server. The external API's `/health` endpoint is anonymous and checks process health; file operations require `X-API-Key`. The main server's `/health/ready` check calls the authenticated `GET /api/files/health` endpoint on the configured External File API, so readiness reports unhealthy if the API is unreachable or its API key is rejected. If External File API storage is not configured, this dependency check reports healthy because the provider is optional. `/health/live` remains a liveness-only check and does not probe dependencies.


## Public HTML Pages

The administrator dashboard supports uploading and publishing standalone HTML pages. See [Public HTML Pages documentation](docs/public-html-pages.md) for API routes, slug rules, security isolation, storage behavior, and deployment notes.
