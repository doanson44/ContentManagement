# Proxy Management

## Overview

The administrator-only Proxy Management page reads the public Vietnam proxy list from ProxyScrape. The page supports search, protocol filtering, pagination, and copying individual or filtered addresses.

## API behavior

- Browser route: `/dashboard/proxies`
- Server endpoint: `GET /api/proxies`
- Upstream: `https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn`
- The server caches a successful parsed result for 30 seconds.
- Upstream requests time out after 15 seconds and responses are limited to 2 MiB.
- The server accepts only IP-address endpoints with HTTP, HTTPS, SOCKS4, or SOCKS5 schemes. Invalid entries are discarded and duplicates are removed.

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
