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
    [switch]$PacketLog = $true
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
$env:PROXY_ROUTES  = "19010:Login:${Server}:9010:bridge;19013:WorldManager_0:${Server}:9013:bridge;19019:Zone_0_0:${Server}:9019:bridge"
$env:PUBLIC_IP     = $Advertise
$env:XOR_TABLE_PATH = "C:/Projects/ik-fiesta-bots/xor-table.hex"
$env:PROXY_PACKET_LOG = if ($PacketLog) { "1" } else { "0" }

$env:FIESTAPROXY_PLUGIN_BRIDGE2026_LOGIN_PORT  = "19010"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE   = $Advertise
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_PORT_OFFSET = "10000"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_CHECKSUMS   = "$root/deploy/bridge2026/zone-checksums.txt"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_ITEM_CLASSES = "$root/deploy/bridge2026/item-classes.txt"
$env:FIESTAPROXY_PLUGIN_BRIDGE2026_OPCODES     = "$root/lib/FiestaLib-Reloaded/docs/extracted/merged/all-enums.json"

Push-Location $run
try { dotnet FiestaProxy.dll } finally { Pop-Location }
