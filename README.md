# fiesta-proxy

A small, dependency-light TCP proxy for **Fiesta Online** server stacks. It
does two jobs, either or both at once:

1. **Client-facing rewriter** — sits in front of Login / WorldManager / Zone and
   rewrites the IP+port fields the servers advertise to the client, so a single
   externally-routable address (or DNS name) can front a stack that internally
   addresses itself on a private bridge / pod network.
2. **Server-to-server (s2s) tunnel** — makes every peer look like `127.0.0.1` to
   the exe, so Fiesta's source-IP allowlist passes trivially and there's no
   boot-time DNS race. Resolves the real peer fresh per connection.

It's the networking glue behind [**fiesta-docker**](https://github.com/IkaronClaude/fiesta-docker)
(BYO Docker/Kubernetes images for Fiesta servers), but builds and runs
standalone too. Open source, runs on **Linux and Windows**, written on top of
[FiestaLib-Reloaded](https://github.com/IkaronClaude/FiestaLib-Reloaded) (vendored
as a submodule) for protocol framing.

## Why this exists

Fiesta's process model is awkward to network: every service binds *and
advertises* an address to the client, and the cluster talks to itself on
`127.0.0.1`. The usual workaround is host networking with fragile port juggling.
fiesta-proxy removes that constraint — the servers can sit on internal-only
addresses and never leak them, because the proxy rewrites the announced
endpoints on the way out and tunnels peer traffic so it always looks local.

## Client-facing rewriters

Two server→client announcement packets carry an endpoint the client then dials.
The proxy parses those frames and patches the endpoint to the public address:

- `PROTO_NC_USER_WORLDSELECT_ACK` (0x0C0C) — Login → client: the WM endpoint.
- `PROTO_NC_CHAR_LOGIN_ACK` (0x1003) — WM → client: the Zone endpoint.

Both are validated end-to-end with `tools/session_client.py` driving a real
Login → WM → Zone chain.

## Traffic model

- **client → server** is XOR-encrypted (after the S→C `NC_MISC_SEED_ACK`
  handshake). The proxy pumps these bytes opaquely — no rewrite hook reads them
  today, so the cipher isn't needed in the hot path.
- **server → client** is plaintext on the wire. On a `rewrite` route the proxy
  parses frames, applies any matching rewriter, and writes the (possibly
  resized) frame to the client.

A route can also run in **`opaque`** mode: no framing, no rewriters, just a raw
byte pump in both directions. Use it for the in-game **client → Zone** channel —
the Zone endpoint was already patched in the WM channel's `CHAR_LOGIN_ACK`, so
the Zone connection carries nothing to rewrite and it's the noisiest connection
in the stack. See the `mode` field of `PROXY_ROUTES` below.

Both directions disable Nagle. Pumps run to natural EOF (no force-close) so
in-flight bytes aren't lost to an RST.

### Upstream boot races (health-gated listeners)

A Fiesta exe treats its first s2s connect as a readiness probe: if it succeeds,
the exe immediately spins up its full parallel connection pool. So the proxy
must NOT have a listen port open before the real upstream is up — a
falsely-successful probe makes the exe flood a not-ready peer and wedge in a
boot loop (a Zone likewise throws *unable to connect to world server* on a
connect-then-drop).

Every listener (s2s inbound, s2s outbound, client-facing) therefore
**health-gates**: it probes its upstream first and only calls `listen()` once
the upstream accepts a connection. Until then the port is closed, so a probe
gets a retryable *connection refused* — exactly what the exe (or a player's
client) expects from a server that hasn't booted yet. Per-connection dials are
then a single attempt (`UPSTREAM_CONNECT_TIMEOUT_SECONDS`, default 10) with no
holding/retry.

## Configuration (env)

The proxy runs in **either or both** modes. Set `PROXY_ROUTES` for the
client-facing rewriter, `S2S_ROUTES` for the s2s tunnel; at least one is
required.

| var | mode | purpose |
| --- | --- | --- |
| `PROXY_ROUTES` | client | `;`-separated `listen:service:upstream:port[:mode]`. `mode` is `rewrite` (default) or `opaque`. Example below. |
| `PUBLIC_HOST` | client | Hostname DNS-resolved to an IPv4 **at startup** and advertised to clients. Lets you point at a name (e.g. an LB) instead of a literal. Takes priority over `PUBLIC_IP`; falls back to it if it doesn't resolve. |
| `PUBLIC_IP` | client | Default external address advertised when no per-service `EXTERNAL_HOST_*` is set. Required if `PROXY_ROUTES` is set and `PUBLIC_HOST` is unset/unresolvable. |
| `EXTERNAL_HOST_<service>` | client | Override the address advertised to clients for `<service>`. |
| `EXTERNAL_PORT_<service>` | client | Override the port advertised to clients for `<service>`. |
| `S2S_ROUTES` | s2s | `;`-separated `bind:port:upstream:port`. Bind `127.0.0.1` = outbound (local exe → peer pod); bind `0.0.0.0` = inbound (peer pod → my exe). No rewriters, no cipher — pure byte passthrough. |
| `S2S_ALLOWED_CIDRS` | s2s | Comma-separated CIDR allowlist; enforced on **inbound** (`0.0.0.0`) listeners only. Default: RFC1918 + loopback + link-local. |
| `UPSTREAM_CONNECT_TIMEOUT_SECONDS` | both | Per-attempt timeout for one upstream TCP connect (health-gate probe + per-connection dials). Default `10`. |
| `PROXY_PACKET_LOG` | both | `1` enables the per-frame trace (opcodes, rewrites, s2s pumps). Off by default; structural events (boot, health, listener-open) log regardless. |
| `XOR_TABLE_HEX` | client | (BYO) Inline hex of the C→S cipher table (whitespace / commas / `0x` ok). Not read by any current rewriter, so the proxy boots without it — but it's part of a working Fiesta deployment, and any future C→S inspection hook throws if it's absent. |
| `XOR_TABLE_PATH` | client | (BYO) Path to a file with the XOR table as hex text or raw binary. Hex is tried first, then binary. |

Setting only `S2S_ROUTES` (no `PROXY_ROUTES`) means `PUBLIC_*`, rewriter, and
XOR config are all unused — pure s2s tunnel mode, which is exactly how
fiesta-docker bakes the proxy into the server runtime image alongside each exe.

The XOR table is **bring-your-own** — different server builds ship different
tables, and this repo ships none. See
`src/FiestaProxy/Crypto/FiestaXorCipher.cs` for the cipher contract.

`PROXY_ROUTES` example:

```
PROXY_ROUTES=9010:Login:login:9010;9015:WorldManager_0:worldmanager:9015;9019:Zone_0_0:zone00:9019:opaque
```

Listen ports are what players connect to. The upstream host is resolved fresh on
every connection (and every packet rewrite that needs an address) so DNS-based
scale events propagate without a restart. The trailing `:opaque` on the Zone
route skips frame parsing — see *Traffic model* above.

## Build

```bash
# Linux
docker build -t fiesta-proxy:linux .

# Windows
docker build -t fiesta-proxy:windows -f Dockerfile.windows .
```

Or build the .NET project directly with the SDK (`dotnet build`); see
`Dockerfile` for the target framework and publish flags.

## Submodule

FiestaLib-Reloaded is vendored as a Git submodule. After cloning:

```bash
git submodule update --init --recursive
```

## License & content

Open source. **No copyrighted game content lives in this repo** — no exes, no
data files, and no cipher table. Anything Fiesta-derived (notably the XOR table)
is bring-your-own, supplied at runtime via the env vars above.
