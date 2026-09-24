# Runs the bridge natively on this box, in front of a local 2016 server, for the 2026 client.
#
# Natively rather than in Docker: Docker Desktop on Windows publishes ports to 127.0.0.1 only, so a
# container is unreachable from the game box on the LAN. deploy/bridge2026/ has the compose file for a
# Linux host, where that restriction does not apply.
param(
    # The address the CLIENT reaches this machine on. It is baked into the world-select and zone-link
    # replies, so it has to be the LAN address, never 127.0.0.1.
    [string]$Advertise = "172.25.164.128",
    [string]$Server    = "127.0.0.1",
    # The C->S cipher table (BYO - this repo ships none). Also read from $env:XOR_TABLE_PATH.
    [string]$XorTable  = $(if ($env:XOR_TABLE_PATH) { $env:XOR_TABLE_PATH } else { "C:/Projects/ik-fiesta-bots/xor-table.hex" }),
    [switch]$PacketLog = $true,
    # Payload bytes shown per logged frame. 0 = the whole packet, which is what a bridge under test wants;
    # pass 48 to get the old short lines back on a busy zone.
    [int]$PacketLogBytes = 0,
    # For a client patched with client-2026-npc-dialog-self-close: stop sending it 0x442E after each
    # quest-page ack, so the patch is what is being tested and not the bridge.
    [switch]$NoDialogClose
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$run  = Join-Path $root "run"

# Publish the proxy and the plugin side by side; the host loads plugins/*.dll next to itself.
dotnet publish "$root/src/FiestaProxy/FiestaProxy.csproj" -c Release -o $run | Out-Null
dotnet build   "$root/plugins/Bridge2026/Bridge2026.csproj" -c Release | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run "plugins") | Out-Null
Copy-Item "$root/plugins/Bridge2026/bin/Release/net10.0/Bridge2026.dll" (Join-Path $run "plugins") -Force

# Listener ports are the server's plus PORT_OFFSET, so the client's own world-select reply points back here.
# listen:service:upstream:port:mode, semicolon separated. Bridge mode decodes BOTH directions, which the
# plugin needs: a rewrite route only ever sees the server side.
#
# EVERY zone needs a route, not just the one the character logs into. A map transition sends
# NC_MAP_LINKOTHER_CMD naming the destination zone's address, the client closes its current connection and
# dials that one; with no listener in front of it there is nothing to dial and the client sits at 0% for
# ever, Not Responding. TevaL is on zone 4 (port 9028) and hung exactly there.
# Ports are PG_W00_Z<nn> in ServerInfo.txt: zone 0 = 9016, 1 = 9019, 2 = 9022, 3 = 9025, 4 = 9028, and the
# listener is that plus PORT_OFFSET so the address handed back to the client points at this proxy.
$env:PROXY_ROUTES  = ("19010:Login:${Server}:9010:bridge;" +
                      "19013:WorldManager_0:${Server}:9013:bridge;" +
                      "19016:Zone_0_0:${Server}:9016:bridge;" +
                      "19019:Zone_0_1:${Server}:9019:bridge;" +
                      "19022:Zone_0_2:${Server}:9022:bridge;" +
                      "19025:Zone_0_3:${Server}:9025:bridge;" +
                      "19028:Zone_0_4:${Server}:9028:bridge")
$env:PUBLIC_IP     = $Advertise
if (-not (Test-Path $XorTable)) { throw "XOR table not found at $XorTable - pass -XorTable <path> (bring your own)" }
$env:XOR_TABLE_PATH = $XorTable
$env:PROXY_PACKET_LOG = if ($PacketLog) { "1" } else { "0" }
$env:PROXY_PACKET_LOG_BYTES = "$PacketLogBytes"
$env:BRIDGE2026_CLOSE_DIALOG = if ($NoDialogClose) { "0" } else { "1" }

$env:FIESTAPROXY_PLUGIN_BRIDGE2026_LOGIN_PORT  = "19010"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE   = $Advertise
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_PORT_OFFSET = "10000"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_CHECKSUMS   = "$root/deploy/bridge2026/zone-checksums.txt"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_ITEM_CLASSES = "$root/deploy/bridge2026/item-classes.txt"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_QUEST_REWARD_INDEX = "$root/deploy/bridge2026/quest-reward-index.txt"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_OPCODES     = "$root/lib/FiestaLib-Reloaded/docs/extracted/merged/all-enums.json"

Push-Location $run
try { dotnet FiestaProxy.dll } finally { Pop-Location }
