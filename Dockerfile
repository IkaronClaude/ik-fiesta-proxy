# syntax=docker/dockerfile:1.7
#
# fiesta-proxy: Linux build of the FiestaLib-Reloaded based packet-rewrite proxy.
# Built as a portable framework-dependent self-contained binary on net10.0.
#
# Configuration is entirely env-var driven; see README.md.
#
# Plugins are published into /app/plugins, which is where the host looks by default
# (FIESTAPROXY_PLUGIN_DIR moves it). Each plugin is built with EnableDynamicLoading and
# Private=false on its references, so only the plugin's own assembly lands there and the
# shared types resolve to the host's copies.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore first so layer cache is reusable across source-only edits.
COPY FiestaProxy.sln ./
COPY src/FiestaProxy/FiestaProxy.csproj src/FiestaProxy/
COPY plugins/Bridge2026/Bridge2026.csproj plugins/Bridge2026/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/FiestaLibReloaded.Networking.csproj lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/
RUN dotnet restore src/FiestaProxy/FiestaProxy.csproj \
 && dotnet restore plugins/Bridge2026/Bridge2026.csproj

COPY src/ src/
COPY lib/ lib/
COPY plugins/ plugins/
RUN dotnet publish src/FiestaProxy/FiestaProxy.csproj -c Release -o /app --no-restore /p:UseAppHost=false \
 && dotnet publish plugins/Bridge2026/Bridge2026.csproj -c Release -o /app/plugins --no-restore /p:UseAppHost=false \
 && rm -f /app/plugins/FiestaProxy.* /app/plugins/FiestaLibReloaded.*

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
WORKDIR /app
COPY --from=build /app/ ./
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "FiestaProxy.dll"]
