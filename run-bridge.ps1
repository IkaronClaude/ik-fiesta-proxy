# Runs the proxy natively on this box, in front of the local stack, for 2016 AND 2026 clients alike.
#
# Natively rather than in Docker: Docker Desktop on Windows publishes ports to 127.0.0.1 only, so a
# container is unreachable from the game box on the LAN.
#
# No plugin since 2026-10-04: the 2026 <-> 2016 translation lives in the servers' own hook plugins (ik-fiesta-patch-
# recipes: login_bridge26, wm_bridge26, bridge26). The proxy only rewrites the server addresses it hands the client
# (WORLDSELECT_ACK 2016/2026, CHAR_LOGIN_ACK, MAP_LINKOTHER - natively, keyed on opcode AND payload size, since opcodes
# are reused between the two client protocols), because it is the client's way into the containers.
param(
    # The address the CLIENT reaches this machine on. It is baked into the world-select and zone-link
    # replies, so it has to be the LAN address, never 127.0.0.1.
    [string]$Advertise = "172.25.164.128",
    [string]$Server    = "127.0.0.1",
    # The C->S cipher table (BYO - this repo ships none). Also read from $env:XOR_TABLE_PATH; only the packet log uses it.
    [string]$XorTable  = $(if ($env:XOR_TABLE_PATH) { $env:XOR_TABLE_PATH } else { "C:/Projects/ik-fiesta-bots/xor-table.hex" }),
    [switch]$PacketLog = $true,
    # Payload bytes shown per logged frame. 0 = the whole packet; pass 48 to get short lines back on a busy zone.
    [int]$PacketLogBytes = 0
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$run  = Join-Path $root "run"

dotnet publish "$root/src/FiestaProxy/FiestaProxy.csproj" -c Release -o $run | Out-Null
# the retired Bridge2026 plugin must not load: it would translate a second time on top of the hooks
Remove-Item (Join-Path $run "plugins/Bridge2026.dll") -ErrorAction SilentlyContinue

# Listener ports are the server's plus 10000, so every address handed back to the client points at this proxy.
# listen:service:upstream:port:mode, semicolon separated; `rewrite` = the native server->client address rewrites.
#
# EVERY zone needs a route, not just the one the character logs into. A map transition sends
# NC_MAP_LINKOTHER_CMD naming the destination zone's address, the client closes its current connection and
# dials that one; with no listener in front of it there is nothing to dial and the client sits at 0% for
# ever, Not Responding. TevaL is on zone 4 (port 9028) and hung exactly there.
# Ports are PG_W00_Z<nn> in ServerInfo.txt: zone 0 = 9016, 1 = 9019, 2 = 9022, 3 = 9025, 4 = 9028.
$env:PROXY_ROUTES  = ("19010:Login:${Server}:9010:rewrite;" +
                      "19013:WorldManager_0:${Server}:9013:rewrite;" +
                      "19016:Zone_0_0:${Server}:9016:rewrite;" +
                      "19019:Zone_0_1:${Server}:9019:rewrite;" +
                      "19022:Zone_0_2:${Server}:9022:rewrite;" +
                      "19025:Zone_0_3:${Server}:9025:rewrite;" +
                      "19028:Zone_0_4:${Server}:9028:rewrite")
$env:PUBLIC_IP     = $Advertise
if (Test-Path $XorTable) { $env:XOR_TABLE_PATH = $XorTable }
$env:PROXY_PACKET_LOG = if ($PacketLog) { "1" } else { "0" }
$env:PROXY_PACKET_LOG_BYTES = "$PacketLogBytes"

Push-Location $run
try { dotnet FiestaProxy.dll } finally { Pop-Location }
