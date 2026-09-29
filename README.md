# ik-fiesta-proxy

A small, dependency-light TCP proxy for **Fiesta Online** server stacks. It
does two jobs, either or both at once:

1. **Client-facing rewriter** — sits in front of Login / WorldManager / Zone and
   rewrites the IP+port fields the servers advertise to the client, so a single
   externally-routable address (or DNS name) can front a stack that internally
   addresses itself on a private bridge / pod network.
2. **Server-to-server (s2s) tunnel** — makes every peer look like `127.0.0.1` to
   the exe, so Fiesta's source-IP allowlist passes trivially and there's no
   boot-time DNS race. Resolves the real peer fresh per connection.

It's the networking glue behind [**fiesta-docker**](https://github.com/IkaronClaude/ik-fiesta-docker)
(BYO Docker/Kubernetes images for Fiesta servers), but builds and runs
standalone too. Open source, runs on **Linux and Windows**, written on top of
[FiestaLib-Reloaded](https://github.com/IkaronClaude/FiestaLib-Reloaded) (vendored
as a submodule) for protocol framing.

## Why this exists

Fiesta's server-to-server links are gated by a **source-IP whitelist** baked into
config: each service only accepts peers whose IP is listed in its `ServerInfo`.
That pins every service to a fixed IP/node at config time — move a process to a
different node, reschedule it, or let it recover from an eviction, and its peers
reject it until you rewrite the whitelist and redeploy.

The baked-in s2s proxy removes that pin. Every peer is reached through a local
proxy, so each exe sees its peers as `127.0.0.1` and the whitelist passes *no
matter where the peer actually runs*; the proxy resolves the real peer fresh per
connection (via DNS). Services can land on arbitrary nodes, reschedule, or
recover from runtime evictions with no config change. The client-facing half does
the mirror trick outward — it rewrites the WM/Zone endpoints the servers advertise
so external clients always reach one public address while the servers keep
internal-only addressing.

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
| `PROXY_ROUTES` | client | `;`-separated `listen:service:upstream:port[:mode]`. `mode` is `rewrite` (default), `opaque`, or `bridge` (both directions decoded and handed to plugins - see [Plugins](#plugins)). Example below. |
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

## Configuration (file) and running as a Windows service

Where there is no environment to set — a registered Windows service, a double-clicked exe — the same settings come
from **`FiestaProxy.conf`** beside the exe (or `--config <file>`). It is translated into the environment variables
above, in-process, before anything starts; a variable already set in the environment wins, so docker / k8s setups are
unaffected. The routes are read from the fronted server's own `ServerInfo.txt`: every client-facing `SERVER_INFO`
row (kind 20, the stock file's `PUBLIC_IP` lines) becomes a route — type 4 `Login`, 5 `WorldManager_<world>`,
6 `Zone_<world>_<zone>` — listening on the server's port + `PORT_OFFSET`.

```
; FiestaProxy.conf   (; = comment, paths relative to this file)
#include "..\ServerSource\9Data\ServerInfo\ServerInfo.txt"   ; the server this proxy fronts
ADVERTISE_IP   192.168.1.10      ; what players dial (-> PUBLIC_IP)
PORT_OFFSET    10000             ; player port = server port + this (default 10000)
MODE           bridge            ; bridge | rewrite | opaque (default bridge)
; LISTEN       Zone_0_3 29025    ; one service's player port, overriding the offset
; UPSTREAM_HOST 10.0.0.5         ; dial the server here instead of ServerInfo's IP
PATH XOR_TABLE_PATH xor-table.hex                            ; PATH = resolved against the conf folder
SET  FIESTAPROXY_PLUGIN_BRIDGE2026_LOGIN_PORT ${LISTEN_Login} ; SET = any variable; ${...} expands the
SET  FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE  ${ADVERTISE_IP} ;   directives, LISTEN_<service>, CONF_DIR
```

```
FiestaProxy.exe --check                      print the variables the conf stands for, and exit
FiestaProxy.exe --install [--name N]         register a Windows service (ADMIN prompt): auto start, restart on
                                             failure; the conf is checked first. N defaults to FiestaProxy
FiestaProxy.exe --uninstall [--name N]       stop + unregister it
```

As a service it logs to `FiestaProxy.log` beside the exe (the previous run is kept as `FiestaProxy.log.1`). If the
proxy cannot start (bad conf, port taken) the service stops, so the SCM shows it and its restart action applies.

## Plugins

A rewriter handles the simple case: one server-to-client opcode, edited in place, no state. Anything more -
state per connection, both directions, packets answered locally instead of relayed, one packet becoming several -
is a **plugin**: a .NET assembly the proxy loads at startup.

**Loading.** Every `*.dll` in `plugins/` beside the proxy executable (or in `FIESTAPROXY_PLUGIN_DIR`) is scanned
for public `IProxyPlugin` implementations with a parameterless constructor
(`src/FiestaProxy/Plugins/IProxyPlugin.cs`). Each assembly gets its own load context. A plugin that fails to load
or initialise is logged and skipped - a bad plugin never stops the proxy from proxying. The startup log names
what loaded: `plugins: loaded bridge2026 from Bridge2026.dll`.

**Settings.** A plugin reads its own environment variables, `FIESTAPROXY_PLUGIN_<NAME>_<KEY>`, with the name
upper-cased and non-alphanumerics turned into `_`. The plugin named `bridge2026` reads
`FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE`, and so on.

**Which traffic it sees.** Plugins are offered every connection, but only a **`bridge`** route decodes the
client's (XOR-encrypted) direction too, so a plugin that needs to read or answer what the client sends must sit
on `bridge` routes - which also need the XOR table (`XOR_TABLE_PATH`). Per packet a plugin can let it pass,
`Drop()` it, `Replace()` it, or queue extra packets `ToClient()` / `ToServer()`.

**Writing one.** Reference `src/FiestaProxy` (see `plugins/Bridge2026/Bridge2026.csproj`), implement
`IProxyPlugin` + `IPluginSession`, build, and copy the DLL into `plugins/`.

### Bridge2026 - the 2026 client on a 2016 server

`plugins/Bridge2026` lets an **unmodified 2026 Fiesta client** (US 10.6.x, or the German build) play on a
**2016 server**. The two builds share a protocol but not its layouts: structs grew, the USER department was
renumbered, and the 2026 client opens with handshakes the 2016 server has never heard of. The plugin sits on
`bridge` routes in front of Login, WorldManager and **every** zone, and translates both directions: the login
and world-list handshakes, the avatar list, map login (checksums swapped for the server's), mob and damage
records, inventory records, the charged-item list, quest dialogs, and more. It drops 2026-only opcodes the 2016
server would hang up on.

It is the client half of **[Fiesta2026on2016](https://github.com/IkaronClaude/Fiesta2026on2016)**, which
builds the server half (the 2026 content merged onto the 2016 data). See that repo's README for the whole setup.

**Run it (Windows, next to the server):**

```powershell
.\run-bridge.ps1 -Advertise <address the CLIENT reaches this machine on> -XorTable <xor-table.hex>
#   -Server 127.0.0.1      where the 2016 server listens (default: this machine)
#   -PacketLog:$false      the per-frame trace is on by default (it is how every layout here was measured)
#   -NoDialogClose         for a client carrying the client-2026-npc-dialog-self-close patch
```

The script publishes the proxy into `run/`, builds the plugin into `run/plugins/`, sets the routes and settings
below, and starts it. Natively rather than in Docker because Docker Desktop on Windows publishes ports to
127.0.0.1 only, which a game client on another machine cannot reach; on Linux, or for a Docker deployment,
`deploy/bridge2026/docker-compose.yml` does the same (settings in `deploy/bridge2026/.env`, see `.env.example`).

Then point the 2026 client at the bridge: `Fiesta.exe -i <advertise address> -p 19010`.

**Routes.** Every listener is the server's port **+10000**, and every endpoint the servers advertise is shifted
by the same `PORT_OFFSET`, so the client always comes back through the bridge. **Every zone needs a route**, not
just the one a character logs into: a map change hands the client the destination zone's address, and with no
listener there the client hangs at 0 % on the loading screen.

```
19010:Login:<server>:9010:bridge;19013:WorldManager_0:<server>:9013:bridge;
19016:Zone_0_0:<server>:9016:bridge;19019:Zone_0_1:<server>:9019:bridge; ... one per zone
```

**Settings** (`FIESTAPROXY_PLUGIN_BRIDGE2026_*`):

| key | purpose |
| --- | --- |
| `LOGIN_PORT` | the listen port that is the login stage (default 9010; the script uses 19010) |
| `ADVERTISE` | the host the client should dial for WM and zones - the client's view of this machine, never 127.0.0.1 |
| `PORT_OFFSET` | added to every port handed to the client (10000) |
| `ITEM_CLASSES` | `item-classes.txt` (required) - see *Data files* |
| `QUEST_REWARD_INDEX` | `quest-reward-index.txt` - see *Data files* |
| `EQUIP_FOLD` | `equip-fold.txt` - see *Data files* |
| `QUEST_COUNTER_ROWS` | `quest-counter-rows.txt` - see *Data files* |
| `OPCODES` | FiestaLib-Reloaded's `docs/extracted/merged/all-enums.json` (required): the opcodes the 2016 build defines; without it nothing is filtered and the server hangs up on the first 2026-only frame |
| `CHECKSUMS` | `zone-checksums.txt` (optional, legacy) - see *Data files* |
| `WORLD_STATUS` | force every world row's status byte (testing only) |

`BRIDGE2026_CLOSE_DIALOG=0` (the script's `-NoDialogClose`) stops the bridge sending the 2026 client its
quest-page close (`0x442E`).

### Data files (generated from YOUR files - never shipped)

This repository ships **no game data**. Everything the plugin needs to know about your server and client is generated
locally from your own files and read at start-up (the files are gitignored; `deploy/bridge2026/` is where the scripts
look). Each is plain text, `#` starts a comment:

| file | line format | what it is | generated from |
| --- | --- | --- | --- |
| `item-classes.txt` | `<item id> <class>` | item id -> the 2026 client's ItemInfo `Class`; the client sizes each inventory record by it | your 2026 client's `ressystem/ItemInfo.shn` |
| `quest-reward-index.txt` | `<quest> <2026 reward row> <2016 reward slot>` | maps the 2026 client's chosen-reward index to the 2016 `QUEST_DATA.Reward` slot (only quests with a choice) | your 2026 client's `QuestReward.shn` + your server's `QuestData.shn` |
| `equip-fold.txt` | `fold <2026 slot> <2016 slot>` / `item <item id> <2026 slot>` | the 2026 equip slots your server folds into 2016 slots, and the items drawn at them (so an unequip clears the right slot) | your 2026 client's `ItemInfo.shn` + your merge's slot map |
| `quest-counter-rows.txt` | `<quest> <row for counter 1> ... <row for counter 5>` | where each zone kill counter sits in the 2026 client's `QuestEndNpc` rows, for quests where they differ | your 2026 client's `QuestEndNpc.shn` + your server's `QuestData.shn` |
| `zone-checksums.txt` | 49 x `<32 hex>` in the zone's order | OPTIONAL, legacy: the table checksums a STOCK 2016 zone compares at map login; with it set, the bridge swaps them into the client's login. Leave it unset when the zone runs the `client_tables` hook plugin (ik-fiesta-patch-recipes), which registers the client's own checksums - a stale file here causes "Client has been illegally manipulated" | your server's `9Data/Shine` (MD5 of header + decrypted body per table) |
| `all-enums.json` (`OPCODES`) | JSON | the protocol's opcode names per department | your server PDBs, via FiestaLib-Reloaded's extractor (see its README) |

The first five are written by `bridge_data.py` from the Fiesta2026on2016 toolset, run against your own trees:

```bash
python tools/bridge_data.py --server <your deployed 9Data> --client26 <your 2026 client root> --out deploy/bridge2026
```

Rerun it whenever your server or client tables change. The formats above are complete, so any tool that reads your
tables can produce the files instead.

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
