---
name: ContentManagement architecture guardrails
description: Mandatory architecture and layering rules for this repository.
applyTo: "**/*.cs,**/*.csproj,**/*.razor,**/*.razor.css,**/*.sln"
---

# Architecture guardrails

- Maintain a modular monolith with exactly two application projects: `ContentManagement.Client` and `ContentManagement.Server`. Unit/integration test projects are permitted.
- Client is Blazor WebAssembly CSR only. Server hosts the built client assets and HTTP APIs in the same deployment. Never introduce Blazor Server, Interactive Server, SignalR UI circuits, a separate SPA, Shared project, RCL, or another app project without explicit approval.
- Dependency direction: client UI -> HTTP API -> application services -> domain rules; application services depend on abstractions, and infrastructure implements those abstractions. Keep controllers/endpoints thin.
- Server owns authentication, authorization, validation, persistence, compression, filesystem access, background work, and audit logging. Never access server infrastructure from browser code.
- SQL Server/EF Core stores metadata and GZIP-compressed UTF-8 JSON as `VARBINARY(MAX)`. Binary payloads live on a configurable filesystem root outside `wwwroot`.
- Avoid new packages and abstractions unless they solve a demonstrated need. Do not introduce microservices, brokers, distributed locks, or additional databases without evidence and approval.
- Before framework-specific changes, inspect target frameworks and package versions and verify current behavior in official documentation.
- If a mandatory architecture constraint conflicts with a safe implementation, stop and explain the trade-off; do not silently change the constraint.