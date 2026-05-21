# fiesta-proxy

FiestaLib-Reloaded based packet-rewrite proxy. Sits in front of one or more
Fiesta server services (Login, WorldManager, Zone, ...) and rewrites the
client-facing IP+port fields in announcement packets so a single externally
routable address (or per-service DNS name) can front a containerised stack
with internal-only addressing.

Status: two rewriters wired against the real docker stack:

- `PROTO_NC_USER_WORLDSELECT_ACK` (0x0C0C) — Login → client: WM endpoint
- `PROTO_NC_CHAR_LOGIN_ACK` (0x1003) — WM → client: Zone endpoint

Both validated end-to-end with `tools/session_client.py` driving a real
Login → WM → Zone chain against the docker stack.

## Traffic model

- **client → server** is XOR-encrypted (after the S→C `NC_MISC_SEED_ACK`
  handshake). The proxy pumps these bytes opaquely — no rewrite hook reads
  them today, so the proxy never needs the cipher in the hot path.
- **server → client** is plaintext on the wire. The proxy parses frames,
  applies any matching rewriter, and writes the (possibly resized) frame to
  the client.

Both directions disable Nagle and force-close on disconnect (see
`src/FiestaProxy/Net/ProxySession.cs`).

## Configuration (env)

| var | purpose |
| --- | --- |
| `PUBLIC_IP` | Default external address used when no per-service `EXTERNAL_HOST_*` is set. |
| `PROXY_ROUTES` | `;`-separated list of `listen:service:upstream:port`. Example below. |
| `EXTERNAL_HOST_<service>` | Override the address advertised to clients for `<service>`. |
| `EXTERNAL_PORT_<service>` | Override the port advertised to clients for `<service>`. |

`PROXY_ROUTES` example:

```
PROXY_ROUTES=9010:Login:login:9010;9015:WorldManager_0:worldmanager:9015;9019:Zone_0_0:zone00:9019
```

Listen ports are what players connect to. Upstream host is resolved fresh on
every connection (and every packet rewrite that needs an address) so DNS-based
scale events propagate without restart.

## Build

```bash
# Linux
docker build -t fiesta-proxy:linux .

# Windows
docker build -t fiesta-proxy:windows -f Dockerfile.windows .
```

## Submodule

FiestaLib-Reloaded is vendored as a Git submodule. After cloning:

```bash
git submodule update --init --recursive
```
