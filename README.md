# ContentManagement

ContentManagement is a modular-monolith foundation for centralized content storage. The browser UI uses Blazor WebAssembly CSR; the ASP.NET Core server hosts the compiled client assets and HTTP API.

## Implemented foundation

- Exactly two application projects: `ContentManagement.Client` and `ContentManagement.Server`.
- Server-hosted Blazor WebAssembly static assets and client-side routing.
- EF Core SQL Server context and initial migration for binary metadata and compressed JSON records.
- GZIP JSON compressor validates JSON, enforces decompressed/uncompressed size limits, and calculates SHA-256 over the original UTF-8 JSON bytes.
- Filesystem storage streams bytes to a temporary file, enforces a size limit, calculates SHA-256, then atomically renames to a server-generated date-partitioned key.
- Lifecycle status enum and SQL row-version concurrency tokens.
- All EF Core enum properties are stored in SQL Server as their symbolic string names; the `EnumValuesAsStrings` migration converts existing numeric file/document statuses.
- API-key authentication via the `X-Content-Management-Key` header and server-side scope policies for files/JSON read, write, and delete operations. All endpoints require authentication by default; health is explicitly anonymous.
- Unit tests for configuration, compression and filesystem storage; Docker SQL Server integration test applies migrations and verifies metadata persistence.
- GitHub Actions build/test pipeline and Windows x86 self-contained ZIP packaging.
- Initial file management API and Blazor WebAssembly page on the `feature/file-management-crud` branch: scoped list/upload/metadata/content/download/rename/soft-delete endpoints, pagination, image/PDF/video previews, bounded text preview, and browser HTTP Range support for media.


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
$env:Authentication__ApiKey = "replace-with-a-long-random-secret"
$env:Authentication__Scopes__0 = "files.read"
$env:Authentication__Scopes__1 = "files.write"
$env:Authentication__Scopes__2 = "json.read"
```

Use a secret manager or protected environment variables in production; never embed this secret in Blazor WebAssembly configuration, JavaScript, appsettings committed to source control, or logs. The server fails at startup if no API key is configured. Send it on server-to-server calls as `X-Content-Management-Key`.

Supported scopes are `files.read`, `files.write`, `files.delete`, `json.read`, `json.write`, and `json.delete`. Grant only required scopes. Apply a policy such as `[Authorize(Policy = ScopePolicies.FilesRead)]` on each corresponding controller action. The fallback authorization policy requires authentication for all endpoints unless explicitly marked `[AllowAnonymous]`.

The static key remains available for backward compatibility. Managed API clients can be created from /dashboard/api-clients; each receives a server-generated client ID and high-entropy secret. The database stores only the SHA-256 hash of the secret. The full key is shown only on creation or rotation and is sent in the X-Content-Management-Key header in the form cm_<client-id>.<secret>. Each managed client has its own file/JSON scopes. Disabled or deleted clients are rejected on subsequent requests. The browser administrator session is separate from API client credentials. This feature uses managed opaque API keys, not OAuth 2.0 Client Credentials; OAuth token issuance remains a future enhancement.

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

## Swagger / OpenAPI

In the Development environment, the server exposes the interactive Swagger UI at `/swagger` and the OpenAPI JSON at `/swagger/v1/swagger.json`. Swagger is disabled outside Development by default to avoid publishing the API surface in production. The document describes both supported authentication alternatives for protected operations: the `X-Content-Management-Key` API-key header and the administrator session cookie. API-key calls still require the relevant file/JSON scope; never enter production secrets on an untrusted machine or embed keys in the Blazor WebAssembly client.

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

The `ContentManagement` section supports `StorageRoot` (filesystem root outside `wwwroot`) and `MaxUploadBytes` (default 100 MiB). JSON documents have no application-configured payload-size limit; Kestrel request-body limiting is disabled. The practical maximum is still bounded by available memory, .NET string/array limits, SQL Server `varbinary(max)` (approximately 2 GB per value), and any IIS/reverse-proxy request limits in front of the app. Size limits should be set at the hosting boundary only when intentionally required to protect capacity. Pending EF Core migrations are applied automatically at server startup.

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

After a successful email OTP sign-in, the client navigates to `/dashboard`. The dashboard verifies the administrator session through `GET /api/auth/me` and redirects unauthenticated visitors to the sign-in page. It provides the workspace overview and navigation layout for API clients, files, and JSON documents. The Files module provides a paginated file list, upload, metadata display, rename, soft-delete, and browser-native previews for supported image/PDF/video types plus small text files. JSON management supports listing, searching, validation, create, update, and permanent delete. API client management supports create/list/update/disable/secret rotation/delete and per-client file/JSON scopes. The Setup page is dedicated to stale-file cleanup policy. SMTP is configured through server-side appsettings or environment variables.


## Managed API client administration

Administrators can use /dashboard/api-clients to create, list, edit, disable, rotate, and delete API clients. Available scopes are files.read, files.write, files.delete, json.read, json.write, and json.delete. Assign only the scopes required by each integration.

A new client receives an API key in the form cm_<client-id>.<secret>. Store the complete key in the consuming application's server-side secret store; it cannot be retrieved from ContentManagement after the create/rotate response. The database stores a SHA-256 hash of the randomly generated 256-bit secret, not the plaintext secret. Rotating the secret invalidates the previous key immediately. Disabling or deleting a client also prevents future authentication.

Managed API clients are authenticated through the existing X-Content-Management-Key header. The configured static API key remains supported for backward compatibility and continues to use its configured scope set. Managed API client scopes are enforced by the same file and JSON authorization policies. Client management endpoints require the administrator cookie and are not available to API-key callers.

This feature does not implement OAuth 2.0 Client Credentials or resource-level ownership isolation. Scope assignment is currently at the API capability level (files and JSON); do not expose the API to untrusted clients without separately implementing and reviewing resource-level authorization, API rate limits, and audit logging.

## Quick Notes

Quick Notes is an administrator-only personal note area available at `/dashboard/quick-notes`. It stores plain-text titles and content in SQL Server, independently of file and JSON content. The dashboard includes a Quick Notes entry for the signed-in administrator.

- Notes can be created, searched by title/content, pinned, edited, and deleted.
- Title length is limited to 200 characters; note content is limited to 20,000 characters.
- Edits are sent after a 650 ms debounce. The UI reports pending, saving, saved, and failed states; failed edits remain in the editor.
- Updates use SQL Server row-version concurrency checks. A stale update returns HTTP 409 rather than silently overwriting a concurrent change.
- The API is under `/api/admin/quick-notes` and requires the administrator cookie and `Administrator` role. It is not available to API-key clients.
- Migration `202610120001_AddQuickNotes` creates the notes table and its ordering index. The server applies pending EF Core migrations at startup, so back up the database and review the migration before deployment.

Quick Notes currently supports the application's configured administrator session, not multiple independent user accounts. Content is plain text; rich text, attachments, sharing, reminders, and note version history are not implemented.


## Responsive layout

The client loads `wwwroot/css/responsive.css` after the base `app.css` stylesheet. The responsive layer refines the shared content width, dashboard navigation wrapping, file list/preview layout, table overflow, setup forms, modal viewport constraints, and Quick Notes layout without adding an application project or changing Blazor WebAssembly CSR.

Responsive behavior is designed around content width rather than device detection:

- Wide desktop: dashboard navigation and file preview can use multi-column layouts.
- Tablet and compact desktop: file workspace stacks when side-by-side panels become constrained; dashboard navigation wraps.
- Mobile: content and forms use a single column, toolbar controls wrap, file/JSON/API-client tables become labeled record cards, modals respect the dynamic viewport, and Quick Notes places the list above the editor.
- Reduced-motion preferences are respected by limiting animations and transitions.

### Responsive verification checklist

Before release, verify the application in browser device emulation and at least one real touch device. Suggested viewport widths: 320, 375, 390, 430, 768, 820, 1024, 1280, and 1440 CSS pixels.

Check dashboard navigation, file search/upload/list/preview, JSON cards and row actions, API-client cards, setup forms, CRUD modals, Quick Notes list/editor/save actions, keyboard focus, and page-level horizontal overflow. On mobile, verify every table value has a visible field label and action buttons remain usable; desktop tables retain their tabular layout.

This checklist documents the intended verification procedure; it is not a claim that every viewport or real device has already been tested.
