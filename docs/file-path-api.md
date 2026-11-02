# Managed file storage API

The Server exposes a logical, path-based file API for server-to-server integrations. A consuming system stores its relative `filePath` and does not need to retain Content Management's internal file ID. The physical filesystem path and storage key are never exposed.

## Authentication and ownership

Send the managed API key in `X-Content-Management-Key`. Keep it in the integrating system's server-side secret store; never embed it in browser code.

Each managed API client has an isolated file namespace:

- The authenticated `client_id` claim is the source of the client identity. A request cannot choose its own owner.
- The database stores `OwnerId = ClientId` and `Type = Client` on newly uploaded client-owned files.
- The server stores the path under `clients/{ClientId}/{relativeFilePath}`, while API responses expose the caller's relative `filePath`.
- List, metadata, content, download, rename, restore, overwrite, and delete operations are scoped by both `Type` and `OwnerId`. A client cannot access another client's file by ID or path.
- The `Type` and `OwnerId` fields are assigned by the server and are never accepted from upload requests.
- The administrator session has a separate policy and can manage files across owners.

## Upload

```http
POST /api/files
X-Content-Management-Key: <server-side-key>
Content-Type: multipart/form-data

file: <binary>
filePath: products/product-001/image-01.jpg
override: false
```

- `file` is required and must be non-empty.
- `filePath` is optional. If omitted, the server generates a path in the caller's namespace.
- `override` is optional and defaults to `false`.
- Caller-supplied paths must be relative, no longer than 450 characters after namespace expansion, and contain no empty, dot, dot-dot, control-character, or filesystem-reserved segments.
- Do not include the server-managed `clients/{ClientId}/` prefix in the request path.

When `override=false`, an existing path returns `409 Conflict`. When `override=true`, the server replaces an active file only within the authenticated owner's scope. The new binary is fully written before metadata is switched; the old binary is removed after the metadata update. A failed upload or metadata update leaves the existing file record and binary intact. Concurrent metadata changes return `409 Conflict`.

Files marked for deletion cannot be overwritten or implicitly restored.

## Resolve metadata

```http
GET /api/files/by-path?path=products%2Fproduct-001%2Fimage-01.jpg
X-Content-Management-Key: <server-side-key>
```

## Read or download content

```http
GET /api/files/by-path/content?path=products%2Fproduct-001%2Fimage-01.jpg
GET /api/files/by-path/download?path=products%2Fproduct-001%2Fimage-01.jpg
X-Content-Management-Key: <server-side-key>
```

## List and ID-based operations

The existing ID-based routes remain available and are ownership-scoped for managed API clients:

- `GET /api/files` — list only the caller's files.
- `GET /api/files/{id}` — metadata.
- `GET /api/files/{id}/content` and `GET /api/files/{id}/download` — content.
- `PATCH /api/files/{id}` — rename the display filename.
- `POST /api/files/{id}/restore` — restore a file marked for deletion.
- `DELETE /api/files/{id}` — mark a file for deletion.

Path-based deletion is also available:

```http
DELETE /api/files/by-path?path=products%2Fproduct-001%2Fimage-01.jpg
X-Content-Management-Key: <server-side-key>
```

Deletion uses the existing soft-delete lifecycle. A successful request returns `202 Accepted`; binary cleanup occurs later through the configured lifecycle worker. Deleting a missing or out-of-scope path is idempotent and returns `204 No Content`.

## Scopes

| Operation | Required scope |
|---|---|
| List, metadata, content, download | `files.read` |
| Upload, overwrite, rename, restore | `files.write` |
| Delete by ID or path | `files.delete` |

Assign only the scopes required by each integration.

## Ownership metadata and migration

`Files.Type` is stored as a string enum:

- `Legacy` — existing records whose owner cannot be reliably inferred.
- `User` — files uploaded by a signed-in user/admin identity.
- `Client` — files owned by a managed API client.
- `System` — reserved for future system-generated files.

The additive migration preserves existing `OwnerId` values. Existing records with a non-null `OwnerId` are classified as `User`; records with a null owner are classified as `Legacy`. Binary files are not moved, and existing logical paths are not rewritten.

## Compatibility and behavior

- The global unique index on `FilePath` remains in place. Client namespaces allow different clients to use the same relative path.
- Renaming a file changes its display filename, not its logical path.
- Only active files can be resolved by path or served.
- The returned `filePath` is the stable relative integration reference. Do not construct paths from the physical storage directory or assume that a path is a disk path.
- URL-encode the `path` query value. The server normalizes backslashes to forward slashes and rejects traversal and ambiguous paths.
