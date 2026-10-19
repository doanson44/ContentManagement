# Public HTML Pages

## Scope

Public HTML Pages allows administrators and API clients with existing file scopes to upload one static HTML document, manage metadata, and publish it at `/html/{slug}`. It does not include an HTML editor, version history, old-slug aliases, or multi-file site hosting.

## Behavior

- Accepts `.html` and `.htm` files from 1 byte through 5 MiB.
- Title is required and limited to 200 characters at the API boundary.
- Slugs are normalized to lowercase and must contain 1–80 ASCII letters/digits separated by single hyphens.
- New pages are unpublished by default.
- Invalid, unpublished, deleted, and missing public pages return 404.
- Public pages render inside a sandboxed iframe without `allow-same-origin` and use no-referrer behavior.
- Renaming a slug invalidates the old URL; no redirect alias is retained.
- Deletion soft-deletes metadata and unpublishes immediately. Failed binary deletion is retried hourly when Hangfire is enabled.

## API

| Method | Route | Required scope | Purpose |
|---|---|---|---|
| GET | `/api/public-html` | `files.read` | Search/filter/paginate metadata |
| POST | `/api/public-html` | `files.write` | Multipart HTML upload |
| GET | `/api/public-html/{id}` | `files.read` | Read metadata |
| PATCH | `/api/public-html/{id}/slug` | `files.write` | Rename slug |
| PUT | `/api/public-html/{id}/publish` | `files.write` | Publish/unpublish |
| DELETE | `/api/public-html/{id}` | `files.delete` | Delete page |
| GET | `/html/{slug}` | Anonymous | Sandboxed wrapper |
| GET | `/api/public-html/{slug}/content` | Anonymous | Published HTML content |

Administrator sessions are allowed by existing file-scope policies. API clients need the corresponding `files.read`, `files.write`, or `files.delete` scope.

## Storage and security

Metadata is in SQL Server and content is stored through the existing `IFileStorage` abstraction using a server-generated storage key and SHA-256 hash. Server-side validation enforces extension, size, title, slug, and publish state. Public content is only served when published and not deleted. Uploaded HTML is active, untrusted content; the iframe sandbox is not a substitute for a dedicated origin when stronger isolation is required.

## Deployment

The application applies EF Core migrations at startup. When Hangfire is enabled, `public-html-storage-cleanup` retries deletion for soft-deleted pages with pending binary cleanup. Back up SQL Server metadata and filesystem content together.
