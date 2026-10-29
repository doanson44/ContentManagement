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
