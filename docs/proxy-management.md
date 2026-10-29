# Proxy Management

## Overview

The administrator-only Proxy Management page reads the public Vietnam proxy list from ProxyScrape. When metadata is included in the upstream JSON response, the server preserves country/country code, city, anonymity, SSL support, latency, uptime, ASN, ISP, and last-checked time. The page displays those fields and supports sorting by table headers, full-field text search, protocol/country/anonymity/SSL filters, pagination, and copying individual or filtered addresses. Metadata omitted by the upstream response is displayed as an em dash rather than inferred.

## API behavior

- Browser route: `/dashboard/proxies`
- Server endpoint: `GET /api/proxies`
- Upstream: `https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn`
- The server caches a successful parsed result for 30 seconds.
- Upstream requests time out after 15 seconds and responses are limited to 2 MiB.
- The server accepts only IP-address endpoints with HTTP, HTTPS, SOCKS4, or SOCKS5 schemes. Invalid entries are discarded and duplicates are removed. It parses optional metadata fields when present and remains compatible with address-only JSON entries.

## Security and reliability

The server endpoint requires the existing Administrator role. The upstream URL is fixed in server code; clients cannot supply an arbitrary URL. This avoids turning the endpoint into an SSRF proxy. The endpoint does not connect through the listed proxies or claim that they are healthy. The list is public and volatile; do not send credentials or sensitive information through these endpoints.

## Troubleshooting

- `502`: the upstream provider returned an error, invalid JSON, or a response over the size limit.
- `504`: the upstream provider did not respond before the timeout.
- An empty list can mean ProxyScrape currently has no matching Vietnam entries or returned a payload with no supported proxy entries.


## Pinning and automatic failover

- The table `PinnedProxies` stores at most one system-wide pinned proxy. Its fixed primary key (`Id = 1`) enforces the singleton at the database level.
- Use `POST /api/proxies/pin` with `{ "address": "http://203.0.113.10:8080" }` to replace the pin. The current pin is exposed by `GET /api/proxies/pinned`. Both endpoints require the Administrator role.
- The pinned value is mirrored into the server's in-memory cache without an expiration. Pinning another proxy replaces the cached value; automatic failover does the same. SQL Server remains the durable source of truth across process restarts.
- When Hangfire is enabled, `PinnedProxyHealthJob` runs every 15 minutes. It checks TCP reachability and connection latency with a four-second per-proxy timeout. A proxy is considered eligible when its TCP port is reachable in 3 seconds or less.
- If the pinned proxy's TCP connection fails, the job attempts failover immediately regardless of the automatic-swap setting. A slow but reachable pinned proxy is not treated as dead solely because it exceeds the 3-second candidate threshold. The job checks up to 30 alternatives (up to five concurrently) and pins the fastest eligible candidate. If no candidate qualifies, it retains the existing pin and records the failed health result.
- The health check measures TCP reachability only. It does not perform an HTTP request through the proxy or guarantee that the proxy can relay traffic, support TLS, or preserve privacy. Public proxies remain untrusted.
- The recurring job is registered only when `Hangfire:Enabled` is true. Ensure Hangfire is enabled and SQL Server is reachable to run scheduled checks.


## Configurable proxy rotation

- The administrator Setup page includes an `Enable automatic proxy swap` toggle and interval choices: 15, 30, 45, 60 minutes, 4 hours, 1 day, or 1 week.
- The setting is persisted in `SystemSettings.ProxySwapEnabled` and `SystemSettings.ProxySwapIntervalMinutes`; the allowed values are validated by the server.
- The Hangfire job still runs every 15 minutes and checks health on each run. If the pinned proxy is unhealthy or slower than the health threshold, failover is attempted immediately regardless of the rotation toggle.
- If the current proxy is healthy, scheduled rotation occurs only when automatic swap is enabled and the selected interval has elapsed since `PinnedUtc`—the last manual pin or successful automatic rotation. `LastCheckedUtc` is used only for health-check timestamps and does not reset the rotation interval.
- If automatic swap is disabled, a healthy proxy is not rotated merely because the interval elapsed.


## Pinned-proxy request API

- Endpoint: `POST /api/proxy` (Administrator role required).
- The endpoint loads the current pinned proxy from SQL Server and sends the outbound request through it. It does not accept a proxy address from the caller.
- Supported target methods: `GET`, `HEAD`, `POST`, `PUT`, `PATCH`, `DELETE`, and `OPTIONS`. Target URLs must use HTTP or HTTPS and resolve only to public IP addresses. Loopback/private-network targets and automatic redirects are blocked.
- HTTP/HTTPS proxies use the .NET HTTP proxy handler. SOCKS4 and SOCKS5 use a direct SOCKS handshake; SOCKS5 endpoints must permit unauthenticated connections because the pin model does not store proxy credentials.
- Request body limit is 1 MiB; response body limit is 2 MiB. Timeout must be 1–60 seconds. Cache duration must be 1–3600 seconds. The API returns the upstream status, response headers/body, elapsed time, and pinned proxy address inside its JSON response; a timeout returns HTTP 504 and a failed relay returns HTTP 502.
- Response caching is opt-in and applies only to GET requests without Authorization or Cookie headers. Cache keys include the target URL, method, and request headers.
- Example request:

```json
{
  "targetUrl": "https://httpbin.org/anything",
  "method": "GET",
  "headers": { "Accept": "application/json" },
  "queryParameters": { "source": "proxy-test" },
  "body": null,
  "timeoutSeconds": 15,
  "useCache": false,
  "cacheDurationSeconds": 60
}
```

- The `/dashboard/proxies` page includes a test card for submitting this payload and viewing the upstream status, response headers, response body, latency, and proxy used. Keep credentials and sensitive data out of requests: free public proxies can inspect or alter unencrypted HTTP traffic.
