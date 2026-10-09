# ContentManagement

ContentManagement is a modular-monolith foundation for centralized content storage. The browser UI uses Blazor WebAssembly CSR; the ASP.NET Core server hosts the compiled client assets and HTTP API.

## Current baseline

Implemented:
- Separate ContentManagement.Client and ContentManagement.Server application projects.
- Server-hosted Blazor WebAssembly static assets and client-side routing.
- Minimal GET /api/health endpoint.
- Startup validation for baseline content settings.
- Unit tests for configuration validation.
- Docker SQL Server connectivity integration test.
- GitHub Actions build/test pipeline and Windows x86 self-contained ZIP packaging.

File storage, JSON document persistence, authentication, authorization, lifecycle cleanup, and administrative workflows are not implemented yet.

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

The server hosts the client at / and the health endpoint at /api/health. HTTPS redirection may require trusting the local development certificate.

## Configuration

The ContentManagement section supports StorageRoot, a filesystem root intended for future binary storage, and MaxUploadBytes, a positive upload limit defaulting to 100 MiB. These settings are groundwork only; no upload or filesystem storage API is implemented yet. Override through standard ASP.NET Core providers, for example ContentManagement__StorageRoot and ContentManagement__MaxUploadBytes.

## Windows x86 package

The CI workflow publishes the server as a self-contained win-x86 application, includes the hosted WebAssembly assets, validates required output files, and uploads ContentManagement-win-x86.zip as a workflow artifact.

Before production use, configure external SQL Server connectivity, a persistent storage root with appropriate service-account permissions, HTTPS termination, backups, and production secrets. This baseline does not automatically apply database migrations.
