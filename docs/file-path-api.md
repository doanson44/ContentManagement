# File path API

The Server exposes stable logical file paths so an integrating system can store a path instead of an internal file identifier. Paths are logical identifiers; they do not expose the physical filesystem location.

## Upload

```http
POST /api/files
X-Content-Management-Key: <server-side-key>
Content-Type: multipart/form-data

file: <binary>
filePath: products/product-001/image-01.jpg
```

`filePath` is optional. When omitted, the Server generates a stable path such as `files/2026/10/19/<generated-key>`. The upload response includes `filePath`. Store that value in the external system.

A caller-supplied path must be relative, no longer than 450 characters, and contain no empty, dot, dot-dot, control-character, or filesystem-reserved segments. Paths are unique and cannot be reused, including after deletion. Duplicate paths return `409 Conflict`.

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

All endpoints require the `files.read` scope; upload requires `files.write`. API keys must remain on the integrating system's server and must never be embedded in browser code.

## Compatibility and behavior

- Existing records are migrated to `legacy/<existing-guid>` paths; their binary data is not moved.
- Renaming a file changes its display filename, not its logical path.
- Only active files can be resolved by path or served.
- The returned `filePath` is the stable integration reference. Do not construct paths from the physical storage directory or assume that a path is a disk path.
- URL-encode the `path` query value. The Server normalizes backslashes to forward slashes and rejects traversal and ambiguous paths.
