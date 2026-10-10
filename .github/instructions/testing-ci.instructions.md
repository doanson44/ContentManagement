---
name: ContentManagement testing and CI requirements
description: Testing, CI/CD, publishing, and deployment artifact rules.
applyTo: "**/*Tests/**/*.cs,**/*.csproj,.github/workflows/**/*.yml,.github/workflows/**/*.yaml,Docker/**/*"
---

# Testing and CI requirements

- Add or update unit tests for validation, lifecycle transitions, authorization rules, compression, hashing, cleanup eligibility, configuration, and error mapping as applicable.
- Use real SQL Server in Docker-backed integration tests for SQL Server-specific behavior. Do not substitute EF Core InMemory for integration coverage of SQL semantics or migrations.
- Integration coverage should exercise relevant API authentication/authorization, upload/download, metadata consistency, JSON compression/decompression, cleanup, failure recovery, and concurrency.
- Tests must be deterministic, isolated, and disposable; use temporary filesystem roots and ensure cleanup even when assertions fail. Wait for containers to become healthy rather than relying on arbitrary sleeps.
- Do not silently skip required tests because Docker or a dependency is unavailable. Report the blocker; CI must fail when a mandatory stage cannot run.
- Preserve `.github/workflows/ci.yml` quality gates: restore, Release build, unit tests, Docker SQL Server integration tests, Windows x86 publish, publish-output validation, one ZIP artifact, artifact upload.
- Keep the deployment RID `win-x86` and artifact name `ContentManagement-win-x86.zip` unless explicitly approved otherwise. Confirm the published server contains the expected executable and hosted WASM assets.
- Never package after failed tests. Never hide failures with `|| true`, permissive exit handling, or conditional skips.
- Do not claim a test, build, publish, or deployment passed unless the command/workflow result was actually observed. In final reports distinguish passed, failed, and not run.