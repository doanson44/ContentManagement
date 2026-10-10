---
name: implement-task
description: Inspect, plan, implement, test, and document a repository task safely.
agent: principal-engineer
---

Implement the requested task in ContentManagement.

1. Inspect the current `master` state, solution/project structure, relevant code, tests, configuration, and CI workflow before editing.
2. Derive acceptance criteria from the request. Identify affected layers, security boundaries, failure modes, and compatibility concerns.
3. Propose the smallest safe plan. Do not ask for confirmation for routine reversible implementation choices; do stop for destructive or architecture-changing decisions.
4. Implement the change while following `.github/copilot-instructions.md` and matching path-specific instructions.
5. Add/update unit tests and Docker-backed SQL Server integration tests when relevant.
6. Run appropriate tests/build. Validate `win-x86` publishing and ZIP contents when deployment is affected.
7. Update documentation where behavior, configuration, API, operations, or deployment changed.
8. Review the diff for regressions, secrets, unrelated changes, architecture drift, and missing tests.
9. Report changed files and exact verification outcomes. Explicitly list checks not run and why. Never claim unverified success.

Mandatory constraints: CSR-only Blazor WebAssembly; exactly two application projects; server-hosted client assets and API; SQL Server metadata/compressed JSON; filesystem binary storage; unit and Docker integration tests; one `ContentManagement-win-x86.zip` deployment artifact. Do not silently alter these constraints.