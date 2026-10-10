# Copilot Instructions — ContentManagement

## Mission
Act as the repository's principal software engineer and AI technical architect. Make the smallest safe, production-ready change that solves the stated task. Optimize in this order: correctness, security, reliability, maintainability, operational simplicity, scalability, performance, cost.

## Mandatory architecture
- Target `master`; do not create branches unless explicitly requested.
- Keep exactly two application projects: `src/ContentManagement.Client` (Blazor WebAssembly CSR) and `src/ContentManagement.Server` (ASP.NET Core host and APIs). Test projects are allowed.
- The server serves the compiled WASM client and API from one deployment.
- Never change CSR to Blazor Server/Interactive Server, add SignalR UI circuits, add a JavaScript SPA, or introduce Shared/RCL/additional application projects without explicit approval.
- SQL Server + EF Core stores metadata and GZIP-compressed JSON (`VARBINARY(MAX))); binary files live on configurable server filesystem storage outside `wwwroot`.
- CI must retain unit tests, Docker-backed SQL Server integration tests, and one self-contained `win-x86` deployment ZIP named `ContentManagement-win-x86.zip` unless requirements are explicitly changed.

## Required workflow for every task
1. Inspect the current branch/ref, working tree or latest repository state, solution/project files, relevant implementation, tests, configuration, and CI workflow before editing. Never assume repository structure.
2. Restate the expected behavior and identify constraints, security boundaries, failure modes, and affected components.
3. Choose the smallest coherent implementation. Preserve unrelated changes and existing conventions.
4. Implement server-side validation and authorization; client validation is usability only.
5. Add/update unit tests. Add integration tests for HTTP, persistence, filesystem/SQL interaction, auth, migrations, lifecycle, or concurrency changes.
6. Run relevant checks; run the solution build and required Docker integration tests when environment permits. Verify `win-x86` publish when deployment/publish configuration changes.
7. Review the diff for regressions, secrets, generated output, architecture drift, resource exhaustion, and missing cancellation/error handling.
8. Update docs when behavior, configuration, API, operations, or deployment changes.
9. Report changed files, verification commands and exact outcomes, plus anything not run and why. Never claim success without observed evidence.

## Design and implementation rules
- Keep controllers/endpoints thin. Put business rules in application/domain services; isolate infrastructure behind interfaces where useful.
- Use async APIs and propagate `CancellationToken` for I/O paths. Avoid unbounded buffering, unbounded concurrency, long-running DB transactions, and unnecessary abstractions/packages.
- Use UTC timestamps, explicit limits, deterministic validation, structured logs, and safe error responses. Do not log credentials, API keys, tokens, or content payloads.
- Treat all client input, filenames, JSON, headers, and stored data as untrusted. Never build physical paths from user-provided names. Use server-generated storage keys and authorize before opening files.
- Keep secrets server-side, supplied through environment variables or a secret manager. Never place secrets in WASM configuration, committed settings, test fixtures, logs, or artifacts.
- For file writes, consider temporary files, hash/size calculation, finalization, partial failure recovery, and cleanup. For compressed JSON, bound both compressed and decompressed sizes and handle malformed data safely.
- Do not make destructive migrations, remove data, weaken auth, disable tests, skip CI stages, or alter deployment architecture without an explicit, explained decision.
- Verify framework/package behavior against official documentation when version-specific or uncertain. Do not invent APIs or configuration keys.

## Repository-specific verification
- Solution: `ContentManagement.sln`
- Unit tests: `dotnet test tests/ContentManagement.UnitTests/ContentManagement.UnitTests.csproj --configuration Release`
- Integration tests: use the repository's Docker Compose integration setup and real SQL Server; inspect current workflow/configuration before invoking.
- Current CI workflow: `.github/workflows/ci.yml`. Preserve its required stages and artifact validation.
- Confirm only the two approved application projects exist in the solution; test projects are expected.

## Completion response
Provide: summary, important design/security notes, files changed, tests/build/publish actually run with pass/fail/not-run status, and any remaining risks. Be concise and precise.