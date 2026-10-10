---
name: contentmanagement-workflow
description: Repository-specific workflow for inspecting, implementing, testing, and reviewing ContentManagement changes safely.
---

# ContentManagement Engineering Workflow

Use this workflow for implementation, refactoring, bug fixing, dependency updates, security changes, and CI/CD work in this repository.

## Phase 1 — Inspect
1. Confirm target ref is `master` and inspect the current repository state.
2. Read `ContentManagement.sln`, both application project files, relevant source files, tests, configuration, and `.github/workflows/ci.yml`.
3. Search for existing patterns and tests before introducing new types or packages.
4. Identify trust boundaries, data flow, lifecycle states, compatibility concerns, and failure recovery.
5. Do not overwrite unrelated user changes or assume a clean working tree.

## Phase 2 — Plan
Write a short plan containing:
- Expected behavior and acceptance criteria
- Files/components likely to change
- Security and reliability considerations
- Tests required
- Documentation and CI/publish impact

Keep the plan proportional to task size. For trivial edits, use a brief checklist rather than a design document.

## Phase 3 — Implement
1. Make the smallest complete change that meets the acceptance criteria.
2. Preserve the modular-monolith architecture and two application-project limit.
3. Keep business rules out of UI components and controllers.
4. Validate and authorize at the server boundary; propagate cancellation through I/O.
5. Add explicit limits and safe failure behavior for untrusted or potentially large data.
6. Avoid unrelated formatting, broad rewrites, speculative features, and unnecessary dependencies.
7. Update tests alongside the implementation.

## Phase 4 — Verify
1. Inspect the diff and check for accidental changes, secrets, generated files, and architecture drift.
2. Run focused unit tests, then broader tests/build when practical.
3. For persistence, HTTP, auth, filesystem lifecycle, migrations, or concurrency changes, run/add Docker-backed SQL Server integration tests.
4. For CI/publish/deployment changes, validate the workflow and the `win-x86` publish/ZIP contents.
5. If a check cannot run, record the exact command and reason. Do not imply it passed.
6. Do not weaken or bypass a failing test just to obtain a green result; diagnose the underlying issue.

## Phase 5 — Review
Check:
- Correctness, edge cases, cancellation, idempotency, concurrency
- Authentication, authorization, path traversal, injection, XSS/CSRF, resource exhaustion
- Data compatibility, migrations, rollback and partial failure
- Logging, metrics, secret/content exposure
- Test quality and documentation accuracy
- Exactly two application projects and one deployment ZIP

## Phase 6 — Deliver
Report:
- What changed and why
- Important design/security decisions
- Tests/build/publish actually run and their exact status
- Checks not run and blockers
- Documentation changed
- Remaining risks or follow-up work

## Stop conditions
Stop and request a decision before proceeding if a task requires:
- Changing a mandatory architecture/deployment requirement
- A destructive data migration or irreversible data loss
- Disabling authentication, authorization, tests, or CI quality gates
- Adding an application project, major dependency, external infrastructure, or a new auth model beyond the requested scope
- A security trade-off that cannot be resolved safely within the task

Do not stop for routine implementation details that can be decided from existing code and official documentation.