# ContentManagement

ContentManagement is a modular-monolith foundation for centralized content storage. The browser UI uses Blazor WebAssembly CSR; the ASP.NET Core server hosts the compiled client assets and HTTP API.

## Implemented foundation

- Exactly two application projects: `ContentManagement.Client` and `ContentManagement.Server`.
- Server-hosted Blazor WebAssembly static assets and client-side routing.
- EF Core SQL Server context and initial migration for binary metadata and compressed JSON records.
- GZIP JSON compressor validates JSON, enforces decompressed/uncompressed size limits, and calculates SHA-256 over the original UTF-8 JSON bytes.
- Filesystem storage streams bytes to a temporary file, enforces a size limit, calculates SHA-256, then atomically renames to a server-generated date-partitioned key.
- Lifecycle status enum and SQL row-version concurrency tokens.
- Unit tests for configuration, compression and filesystem storage; Docker SQL Server integration test applies migrations and verifies metadata persistence.
- GitHub Actions build/test pipeline and Windows x86 self-contained ZIP packaging.

Authentication/authorization, public file/JSON APIs, complete lifecycle orchestration, cleanup/recovery jobs, and administrative workflows are not implemented. Storage and compression services are internal foundations and must not be exposed without server-side authorization and resource-level access checks.

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

```bash
dotnet run --project src/ContentManagement.Server/ContentManagement.Server.csproj
```

The server hosts the client at `/` and the health endpoint at `/api/health`. HTTPS redirection may require trusting the local development certificate.

## Configuration

The `ContentManagement` section supports `StorageRoot` (filesystem root outside `wwwroot`), `MaxUploadBytes` (default 100 MiB), and `MaxJsonDocumentBytes` (default 10 MiB). Override through standard providers such as `ContentManagement__StorageRoot`, `ContentManagement__MaxUploadBytes`, and `ContentManagement__MaxJsonDocumentBytes`. Configure SQL with `ConnectionStrings__ContentManagement`. Migrations are not applied automatically; apply reviewed migrations during deployment after a backup.

## Data and storage notes

- Binary bytes are stored in date-partitioned directories under opaque server-generated keys; metadata is in SQL Server.
- JSON payloads are intended for GZIP-compressed UTF-8 storage in SQL Server `varbinary(max)`.
- SHA-256 is integrity metadata, not an authentication token.
- Filesystem writes and SQL metadata writes cannot share one atomic transaction. An application service must reconcile orphaned binaries and incomplete metadata writes.
- Do not expose storage services through APIs until authentication, authorization, rate limits, and resource ownership checks exist.

## Windows x86 package

CI publishes the server as a self-contained `win-x86` application, includes hosted WebAssembly assets, validates required output files, and uploads `ContentManagement-win-x86.zip` as a workflow artifact.

Before production use, configure external SQL Server connectivity, a persistent storage root with appropriate service-account permissions, HTTPS termination, backups, and production secrets. This baseline does not automatically apply database migrations.
