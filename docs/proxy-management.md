# Proxy Management

## Overview

The administrator-only Proxy Management page reads the public Vietnam proxy list from ProxyScrape. When metadata is included in the upstream JSON response, the server preserves country/country code, city, anonymity, SSL support, latency, uptime, ASN, ISP, and last-checked time. The page displays those fields and supports sorting by table headers, full-field text search, protocol/country/anonymity/SSL filters, pagination, and copying individual or filtered addresses. Metadata omitted by the upstream response is displayed as an em dash rather than inferred.

## API behavior

- Browser route: `/dashboard/proxies`
- Server endpoint: `GET /api/proxies`
- Upstream: `https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn&timeout=5000`
- The `timeout=5000` provider filter drops endpoints that advertise more than five seconds of response time; those endpoints cannot satisfy the relay and health-check budgets used by this server.
- The server caches a successful parsed result for 30 seconds.
- Upstream requests time out after 15 seconds and responses are limited to 2 MiB.
- The server accepts only IP-address endpoints with HTTP, HTTPS, SOCKS4, or SOCKS5 schemes. Invalid entries are discarded and duplicates are removed. It parses optional metadata fields when present and remains compatible with address-only JSON entries.

## Security and reliability

The server endpoint requires the existing Administrator role. The upstream URL is fixed in server code; clients cannot supply an arbitrary URL. This avoids turning the endpoint into an SSRF proxy. The `GET /api/proxies` endpoint only lists proxies and does not test relay capability. The separate `POST /api/proxy` endpoint uses the pinned proxy for outbound requests and applies target URL safeguards. The list is public and volatile; do not send credentials or sensitive information through these endpoints.

## Troubleshooting

- `502`: the upstream provider returned an error, invalid JSON, or a response over the size limit.
- `504`: the upstream provider did not respond before the timeout.
- An empty list can mean ProxyScrape currently has no matching Vietnam entries or returned a payload with no supported proxy entries.


## Pinning and automatic failover

- The table `PinnedProxies` stores at most one system-wide pinned proxy. Its fixed primary key (`Id = 1`) enforces the singleton at the database level.
- Use `POST /api/proxies/pin` with `{ "address": "http://203.0.113.10:8080" }` to replace the pin. The current pin is exposed by `GET /api/proxies/pinned`. Both endpoints require the Administrator role.
- The pinned value is mirrored into the server's in-memory cache without an expiration. Pinning another proxy replaces the cached value; automatic failover does the same. SQL Server remains the durable source of truth across process restarts.
- When Hangfire is enabled, `PinnedProxyHealthJob` runs every two minutes. It sends a `GET` request to a fixed, credential-free probe URL through the proxy and records the end-to-end latency. A proxy is healthy only when a probe completes within `ProxyHealth:ProbeTimeoutSeconds` (default 8); a failed attempt is retried up to `ProxyHealth:ProbeSamples` times (default 2) so one transient hiccup does not trigger a swap.
- Every run also refreshes an in-memory **verified pool** (`ProxyPool`). Entries that were probed more than `ProxyHealth:RevalidateMinutes` ago are rechecked first, then new directory entries are added until the pool holds `ProxyHealth:PoolSize` proxies (default 10). At most `ProxyHealth:MaxProbesPerRun` probes run per cycle, five concurrently, and only one proxy per host IP is kept so a single multihomed host cannot fill the pool.
- Each pool entry carries a score derived from recent probe and relay samples: squared success rate, median latency of the last five successful samples, a penalty per consecutive failure, and a factor for proxies that were never verified. The pinned proxy is replaced by the best entry only when the probe fails, when the rotation interval is due, or when the candidate scores more than `ProxyHealth:ImprovementThreshold` times better (default 1.25). The margin prevents swapping between proxies of similar quality.
- `/api/proxy` feeds the pool with passive samples: a completed exchange counts as success and a timeout, connection failure, or lost connection counts as failure. After `ProxyHealth:PassiveFailureThreshold` consecutive failures (default 2) the entry is marked suspect and the failover job is started off-schedule, subject to a 30-second cooldown and a single-flight gate shared with the recurring job.
- A timeout caused by a slow target also counts as a failure for the proxy. The next successful probe clears the suspect flag, so a false positive costs one extra probe rather than a permanent switch.
- A reachable TCP port is not treated as healthy: free public proxies frequently accept connections without forwarding traffic, so TCP-only checks previously pinned endpoints that could not relay requests.
- The probe proves relay capability, not privacy. The proxy observes the probe request, so keep `ProxyHealth:ProbeUrl` pointed at a public, credential-free endpoint and never send credentials or personal data through these proxies.
- Configuration: `ProxyHealth:ProbeUrl` (default `https://example.com/`), `ProxyHealth:ProbeTimeoutSeconds` (3–30, default 8), `ProxyHealth:MaximumRelayLatencyMilliseconds` (1000–60000, default 8000), `ProxyHealth:ProbeSamples` (1–3, default 2), `ProxyHealth:PoolSize` (3–50, default 10), `ProxyHealth:MaxProbesPerRun` (1–50, default 12), `ProxyHealth:RevalidateMinutes` (1–60, default 5), `ProxyHealth:PassiveFailureThreshold` (1–5, default 2), and `ProxyHealth:ImprovementThreshold` (1.0–5.0, default 1.25). Values outside those ranges fail startup validation.
- Administrators can inspect the current ranking at `GET /api/proxies/pool`, which returns the pinned address and each entry's score, success rate, median latency, and suspect flag.
- The pool lives in memory and is rebuilt by the next job run after a restart. SQL Server remains the durable source of truth for the pin itself.
- No configuration can guarantee that a free public proxy stays usable at all times. The pool bounds how long traffic keeps flowing through a proxy that stopped working and keeps a ranked replacement ready, so expect occasional `502`/`504` responses from `/api/proxy`.
- The recurring job is registered only when `Hangfire:Enabled` is true. Ensure Hangfire is enabled and SQL Server is reachable to run scheduled checks; the off-schedule trigger also relies on the job being resolvable from DI.


## Configurable proxy rotation

- The administrator Setup page includes an `Enable automatic proxy swap` toggle and interval choices: 15, 30, 45, 60 minutes, 4 hours, 1 day, or 1 week.
- The setting is persisted in `SystemSettings.ProxySwapEnabled` and `SystemSettings.ProxySwapIntervalMinutes`; the allowed values are validated by the server.
- The Hangfire job now runs every two minutes and checks health on each run. If the pinned proxy cannot relay the probe, failover is attempted immediately regardless of the rotation toggle.
- If the current proxy relays traffic, scheduled rotation occurs only when automatic swap is enabled and the selected interval has elapsed since `PinnedUtc`—the last manual pin or successful automatic rotation. `LastCheckedUtc` is used only for health-check timestamps and does not reset the rotation interval.
- A healthy proxy is also replaced when another verified pool entry scores more than the configured improvement margin better; the swap does not wait for the rotation interval in that case.
- If automatic swap is disabled, a healthy proxy is not rotated merely because the interval elapsed.


## Pinned-proxy request API

- Endpoint: `POST /api/proxy` (Administrator session or a managed API client granted the `proxy.request` scope).
- To authorize a machine-to-machine caller, open `/dashboard/api-clients` and grant its API client the `proxy.request` permission. Send the key in the `X-Content-Management-Key` header. The scope is least-privilege and does not grant access to proxy pinning or proxy administration endpoints.
- The endpoint loads the current pinned proxy from SQL Server and sends the outbound request through it. It does not accept a proxy address from the caller.
- Supported target methods: `GET`, `HEAD`, `POST`, `PUT`, `PATCH`, `DELETE`, and `OPTIONS`. Target URLs must use HTTP or HTTPS and resolve only to public IP addresses. Loopback/private-network targets and automatic redirects are blocked.
- HTTP/HTTPS proxies use the .NET HTTP proxy handler. SOCKS4 and SOCKS5 use a direct SOCKS handshake; SOCKS5 endpoints must permit unauthenticated connections because the pin model does not store proxy credentials.
- Request body limit is 1 MiB; response body limit is 2 MiB. Timeout must be 1–120 seconds and defaults to 30 seconds; the dashboard test card also defaults to 30. Working public proxies routinely need 5–20 seconds, so shorter timeouts return HTTP 504 even for usable endpoints. Cache duration must be 1–3600 seconds. The API returns the upstream status, response headers/body, elapsed time, and pinned proxy address inside its JSON response; a timeout returns HTTP 504 and a failed relay returns HTTP 502.
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

- The relay resolves the pinned proxy from the in-memory `PinnedProxyCache` first; it queries SQL Server only on a cache miss and populates the cache from the database fallback. Pinning and the health/failover job update the same cache snapshot.
