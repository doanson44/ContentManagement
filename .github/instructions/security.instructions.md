---
name: ContentManagement security rules
description: Security, authorization, storage, and resource-consumption requirements.
applyTo: "**/*.cs,**/*.razor,**/*.json,**/*.yml,**/*.yaml,**/*.config,**/*.csproj"
---

# Security rules

- Treat HTTP input, filenames, JSON, headers, file metadata, and persisted content as untrusted. Enforce validation and authorization on the server regardless of client-side checks.
- Enforce least privilege and resource-level authorization. Do not weaken fallback authorization or add anonymous endpoints unless the endpoint is intentionally public and the reason is documented.
- Never embed API keys, OAuth secrets, database credentials, private paths, or other secrets in Blazor WASM assets, committed configuration, test data, logs, or deployment ZIPs.
- Never use a client-provided filename or path to construct a physical path. Use server-generated keys, canonicalize and constrain storage paths under the configured root, and authorize before opening a file.
- Stream uploads/downloads. Apply request, file-size, JSON-size, decompression, timeout, and concurrency limits appropriate to the code path. Avoid loading arbitrary-size payloads into memory.
- For filesystem writes, use a temporary location, compute size and SHA-256 while streaming where practical, finalize only after required checks, and define cleanup/recovery for partial failures.
- For compressed JSON, validate JSON before persistence, use UTF-8 and GZIP consistently, bound decompressed size to prevent compression bombs, and handle corrupt payloads without exposing internals.
- Use parameterized EF Core queries; do not concatenate untrusted input into SQL, shell commands, or filesystem paths.
- Protect state-changing cookie-authenticated endpoints against CSRF where applicable. Keep HTTPS, secure cookie settings, and rate limiting in mind for auth and OTP paths.
- Return safe client errors; do not disclose stack traces, secrets, physical paths, tokens, or stored content. Use structured logs without credentials or content payloads.
- Add tests for authorization denial, malformed input, size boundaries, traversal attempts, and partial failure when relevant.