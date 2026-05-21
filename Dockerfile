# syntax=docker/dockerfile:1.7
#
# fiesta-proxy: Linux build of the FiestaLib-Reloaded based packet-rewrite proxy.
# Built as a portable framework-dependent self-contained binary on net10.0.
#
# Configuration is entirely env-var driven; see README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore first so layer cache is reusable across source-only edits.
COPY FiestaProxy.sln ./
COPY src/FiestaProxy/FiestaProxy.csproj src/FiestaProxy/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/FiestaLibReloaded.Networking.csproj lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/
RUN dotnet restore src/FiestaProxy/FiestaProxy.csproj

COPY src/ src/
COPY lib/ lib/
RUN dotnet publish src/FiestaProxy/FiestaProxy.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
WORKDIR /app
COPY --from=build /app/ ./
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "FiestaProxy.dll"]
