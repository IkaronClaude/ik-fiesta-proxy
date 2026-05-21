using FiestaProxy.Crypto;

namespace FiestaProxy.Config;

/// <summary>
/// Per-service routing + advertised-endpoint config, sourced entirely from env vars
/// so the same image can serve every operator topology.
///
/// Wire format (semicolon-delimited list, each entry colon-delimited):
///   PROXY_ROUTES=9010:Login:login:9010;9015:WorldManager_0:worldmanager:9015;9019:Zone_0_0:zone00:9019
///                 ^^^^ listen port (player-facing)
///                      ^^^^^ Fiesta service name (matches start.sh/start.ps1 scheme)
///                            ^^^^^ upstream host (docker service name, resolved every connect)
///                                 ^^^^ upstream port
///
/// For each service the proxy looks up EXTERNAL_HOST_&lt;ServiceName&gt; and
/// EXTERNAL_PORT_&lt;ServiceName&gt; when rewriting outbound packets. Defaults are
/// PUBLIC_IP and the listen port respectively.
/// </summary>
public sealed class ProxyConfig
{
    public required IReadOnlyList<ProxyRoute> Routes { get; init; }
    public required IReadOnlyList<S2sRoute> S2sRoutes { get; init; }
    public required IReadOnlyList<System.Net.IPNetwork> S2sAllowedCidrs { get; init; }
    public required string PublicIp { get; init; }
    /// <summary>
    /// Operator-supplied XOR cipher table for C→S decryption. Null means
    /// the operator didn't supply one — the proxy still works (NullCipher
    /// path) because no current rewriter reads C→S traffic.
    /// </summary>
    public required byte[]? XorTable { get; init; }

    public static ProxyConfig FromEnvironment()
    {
        var routesEnv = Environment.GetEnvironmentVariable("PROXY_ROUTES");
        var s2sEnv = Environment.GetEnvironmentVariable("S2S_ROUTES");
        if (string.IsNullOrWhiteSpace(routesEnv) && string.IsNullOrWhiteSpace(s2sEnv))
            throw new InvalidOperationException("At least one of PROXY_ROUTES or S2S_ROUTES must be set");

        // PUBLIC_IP is only meaningful for client-facing mode (rewriters use it
        // as the default external host). Required iff PROXY_ROUTES is set.
        var publicIp = Environment.GetEnvironmentVariable("PUBLIC_IP") ?? "";
        if (!string.IsNullOrWhiteSpace(routesEnv) && string.IsNullOrWhiteSpace(publicIp))
            throw new InvalidOperationException("PUBLIC_IP env var is required when PROXY_ROUTES is set");

        var routes = ParseRoutes(routesEnv);
        var s2sRoutes = ParseS2sRoutes(s2sEnv);
        var s2sCidrs = ParseS2sCidrs(Environment.GetEnvironmentVariable("S2S_ALLOWED_CIDRS"));
        var xorTable = XorTableLoader.FromEnvironment();

        return new ProxyConfig
        {
            Routes = routes,
            S2sRoutes = s2sRoutes,
            S2sAllowedCidrs = s2sCidrs,
            PublicIp = publicIp,
            XorTable = xorTable,
        };
    }

    private static List<ProxyRoute> ParseRoutes(string? env)
    {
        var routes = new List<ProxyRoute>();
        if (string.IsNullOrWhiteSpace(env)) return routes;
        foreach (var entry in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 4)
                throw new InvalidOperationException($"PROXY_ROUTES entry malformed (expect 4 colon-separated fields): '{entry}'");
            if (!int.TryParse(parts[0], out var listen) || !int.TryParse(parts[3], out var upstreamPort))
                throw new InvalidOperationException($"PROXY_ROUTES entry has non-numeric port: '{entry}'");
            routes.Add(new ProxyRoute(listen, parts[1], parts[2], upstreamPort));
        }
        return routes;
    }

    private static List<S2sRoute> ParseS2sRoutes(string? env)
    {
        // Each entry: bind:port:upstream-host:upstream-port
        //   bind = 0.0.0.0 or 127.0.0.1 (or a specific pod IP)
        // 0.0.0.0 listeners are inbound: enforce S2S_ALLOWED_CIDRS.
        // 127.0.0.1 listeners are outbound from the local exe: skip allow-list.
        var routes = new List<S2sRoute>();
        if (string.IsNullOrWhiteSpace(env)) return routes;
        foreach (var entry in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 4)
                throw new InvalidOperationException($"S2S_ROUTES entry malformed (expect bind:port:upstream:port): '{entry}'");
            if (!System.Net.IPAddress.TryParse(parts[0], out var bindIp))
                throw new InvalidOperationException($"S2S_ROUTES bind is not an IP literal: '{entry}'");
            if (!int.TryParse(parts[1], out var listenPort) || !int.TryParse(parts[3], out var upstreamPort))
                throw new InvalidOperationException($"S2S_ROUTES entry has non-numeric port: '{entry}'");
            routes.Add(new S2sRoute(bindIp, listenPort, parts[2], upstreamPort));
        }
        return routes;
    }

    private static List<System.Net.IPNetwork> ParseS2sCidrs(string? env)
    {
        // Default: RFC1918 private + loopback + link-local. The proxy is
        // intended to sit on a cluster-internal network; non-private source
        // IPs imply something outside the cluster reached the s2s port.
        var defaults = new[] {
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "169.254.0.0/16",
            "127.0.0.0/8",
            "::1/128",
            "fc00::/7",
            "fe80::/10",
        };
        var raw = string.IsNullOrWhiteSpace(env) ? defaults : env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<System.Net.IPNetwork>();
        foreach (var cidr in raw)
        {
            if (!System.Net.IPNetwork.TryParse(cidr, out var net))
                throw new InvalidOperationException($"S2S_ALLOWED_CIDRS entry malformed: '{cidr}'");
            list.Add(net);
        }
        return list;
    }

    /// <summary>Look up the operator-facing endpoint for a service name.</summary>
    public (string Host, ushort Port) ExternalEndpoint(string serviceName, ushort fallbackPort)
    {
        var host = Environment.GetEnvironmentVariable($"EXTERNAL_HOST_{serviceName}") ?? PublicIp;
        var portStr = Environment.GetEnvironmentVariable($"EXTERNAL_PORT_{serviceName}");
        var port = (portStr != null && ushort.TryParse(portStr, out var p)) ? p : fallbackPort;
        return (host, port);
    }

    /// <summary>Find the service name registered for a given listen port (proxy's own listen side).</summary>
    public string? ServiceForListenPort(int listenPort)
        => Routes.FirstOrDefault(r => r.ListenPort == listenPort)?.ServiceName;
}

public sealed record ProxyRoute(int ListenPort, string ServiceName, string UpstreamHost, int UpstreamPort);

/// <summary>
/// One s2s tunnel. Bind 0.0.0.0:port for inbound (other pods → my pod), bind
/// 127.0.0.1:port for outbound (local exe → peer pod). Upstream is resolved
/// fresh per connection so peer-pod IP churn doesn't need a proxy restart.
/// </summary>
public sealed record S2sRoute(System.Net.IPAddress BindAddress, int ListenPort, string UpstreamHost, int UpstreamPort)
{
    public bool IsInbound => BindAddress.Equals(System.Net.IPAddress.Any) || BindAddress.Equals(System.Net.IPAddress.IPv6Any);
}
