using FiestaProxy.Crypto;

namespace FiestaProxy.Config;

/// <summary>
/// Per-service routing + advertised-endpoint config, sourced entirely from env vars
/// so the same image can serve every operator topology.
///
/// Wire format (semicolon-delimited list, each entry colon-delimited):
///   PROXY_ROUTES=9010:Login:login:9010;9015:WorldManager_0:worldmanager:9015;9019:Zone_0_0:zone00:9019:opaque
///                 ^^^^ listen port (player-facing)
///                      ^^^^^ Fiesta service name (matches start.sh/start.ps1 scheme)
///                            ^^^^^ upstream host (docker service name, resolved every connect)
///                                 ^^^^ upstream port
///                                      ^^^^^^ optional mode: "rewrite" (default) or "opaque"
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

    /// <summary>
    /// Per-attempt timeout for a single upstream TCP connect. Used by the
    /// listener health-gate (which probes the upstream and only opens the
    /// listen port once it answers) and by per-connection dials. The gate
    /// loops indefinitely until the peer is up, so this bounds one attempt,
    /// not the overall wait. Set via UPSTREAM_CONNECT_TIMEOUT_SECONDS (10s).
    /// </summary>
    public required TimeSpan UpstreamConnectTimeout { get; init; }

    /// <summary>
    /// Whether to emit the per-packet log line (opcode + payload hex preview)
    /// for every frame the proxy forwards in either direction on either route.
    /// Off by default — the per-packet stream is noisy and only useful when
    /// you're actively debugging. Connection-level logs (accept/close, gate
    /// health, listener opens) stay on regardless. Set with PROXY_PACKET_LOG=1.
    /// </summary>
    public required bool PacketLogEnabled { get; init; }

    public static ProxyConfig FromEnvironment()
    {
        var routesEnv = Environment.GetEnvironmentVariable("PROXY_ROUTES");
        var s2sEnv = Environment.GetEnvironmentVariable("S2S_ROUTES");
        if (string.IsNullOrWhiteSpace(routesEnv) && string.IsNullOrWhiteSpace(s2sEnv))
            throw new InvalidOperationException("At least one of PROXY_ROUTES or S2S_ROUTES must be set");

        // The advertised IP (rewritten into client-facing announcement packets
        // so the client dials the right next hop) is resolved in priority order:
        //   1. PUBLIC_HOST set  -> DNS-resolve it to an IPv4 at startup. Lets the
        //      proxy advertise a stable public endpoint by name (e.g. behind a
        //      TCP ingress / LB) instead of a hardcoded address. Useful when the
        //      proxy runs as several pods and none of them knows the public IP
        //      literally.
        //   2. PUBLIC_IP set    -> use the literal.
        // Only meaningful for client-facing mode; required iff PROXY_ROUTES set.
        var publicHost = Environment.GetEnvironmentVariable("PUBLIC_HOST");
        var publicIp = Environment.GetEnvironmentVariable("PUBLIC_IP") ?? "";
        if (!string.IsNullOrWhiteSpace(publicHost))
        {
            var resolved = ResolvePublicHost(publicHost.Trim());
            if (resolved is not null)
            {
                Log.Info($"PUBLIC_HOST '{publicHost.Trim()}' resolved to advertised IP {resolved}");
                publicIp = resolved;
            }
            else if (!string.IsNullOrWhiteSpace(publicIp))
                Log.Warn($"PUBLIC_HOST '{publicHost.Trim()}' did not resolve — falling back to PUBLIC_IP {publicIp}");
            else
                throw new InvalidOperationException($"PUBLIC_HOST '{publicHost.Trim()}' did not resolve to any IPv4 and no PUBLIC_IP fallback is set");
        }
        if (!string.IsNullOrWhiteSpace(routesEnv) && string.IsNullOrWhiteSpace(publicIp))
            throw new InvalidOperationException("PUBLIC_IP or a resolvable PUBLIC_HOST is required when PROXY_ROUTES is set");

        var routes = ParseRoutes(routesEnv);
        var s2sRoutes = ParseS2sRoutes(s2sEnv);
        var s2sCidrs = ParseS2sCidrs(Environment.GetEnvironmentVariable("S2S_ALLOWED_CIDRS"));
        var xorTable = XorTableLoader.FromEnvironment();
        var upstreamTimeout = ParseUpstreamTimeout(Environment.GetEnvironmentVariable("UPSTREAM_CONNECT_TIMEOUT_SECONDS"));
        var packetLog = ParseBoolFlag(Environment.GetEnvironmentVariable("PROXY_PACKET_LOG"));

        return new ProxyConfig
        {
            Routes = routes,
            S2sRoutes = s2sRoutes,
            S2sAllowedCidrs = s2sCidrs,
            PublicIp = publicIp,
            XorTable = xorTable,
            UpstreamConnectTimeout = upstreamTimeout,
            PacketLogEnabled = packetLog,
        };
    }

    /// <summary>Resolve a hostname to its first IPv4 address, or null on
    /// failure. Used for PUBLIC_HOST so the advertised endpoint can be given
    /// by name (resolved once at startup).</summary>
    private static string? ResolvePublicHost(string host)
    {
        // Already an IP literal? Hand it straight back.
        if (System.Net.IPAddress.TryParse(host, out var literal))
            return literal.ToString();
        try
        {
            var addrs = System.Net.Dns.GetHostAddresses(host);
            var v4 = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (v4 is not null) return v4.ToString();
            // No A record; fall back to the first address (e.g. IPv6) if any.
            return addrs.Length > 0 ? addrs[0].ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ParseBoolFlag(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return false;
        return env.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }

    private static TimeSpan ParseUpstreamTimeout(string? env)
    {
        // Per-attempt timeout for a single upstream TCP connect -- used by the
        // listener health-gate probe and per-connection dials. The gate loops
        // (1s backoff) until the peer is up, so this bounds one attempt only.
        const int defaultSeconds = 10;
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var s) && s > 0)
            return TimeSpan.FromSeconds(s);
        return TimeSpan.FromSeconds(defaultSeconds);
    }

    private static List<ProxyRoute> ParseRoutes(string? env)
    {
        var routes = new List<ProxyRoute>();
        if (string.IsNullOrWhiteSpace(env)) return routes;
        foreach (var entry in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            // 4 fields = rewrite route (default); 5th optional field = mode.
            if (parts.Length is not (4 or 5))
                throw new InvalidOperationException($"PROXY_ROUTES entry malformed (expect listen:service:upstream:port[:mode]): '{entry}'");
            if (!int.TryParse(parts[0], out var listen) || !int.TryParse(parts[3], out var upstreamPort))
                throw new InvalidOperationException($"PROXY_ROUTES entry has non-numeric port: '{entry}'");
            var mode = RouteMode.Rewrite;
            if (parts.Length == 5)
                mode = parts[4].Trim().ToLowerInvariant() switch
                {
                    "opaque" => RouteMode.Opaque,
                    "rewrite" or "" => RouteMode.Rewrite,
                    var other => throw new InvalidOperationException(
                        $"PROXY_ROUTES entry has unknown mode '{other}' (expect 'rewrite' or 'opaque'): '{entry}'"),
                };
            routes.Add(new ProxyRoute(listen, parts[1], parts[2], upstreamPort, mode));
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

/// <summary>How the proxy treats a client-facing route's traffic.</summary>
public enum RouteMode
{
    /// <summary>Parse S→C frames and run the rewriter registry (Login / WM routes).</summary>
    Rewrite,

    /// <summary>
    /// Pure byte passthrough — no framing, no rewriters. For the high-volume
    /// in-game Zone channel: it carries no address fields the proxy needs to
    /// patch (the Zone endpoint was already rewritten upstream in the WM
    /// channel's CHAR_LOGIN_ACK), so parsing every frame is wasted work on
    /// the noisiest connection in the stack.
    /// </summary>
    Opaque,
}

public sealed record ProxyRoute(
    int ListenPort, string ServiceName, string UpstreamHost, int UpstreamPort,
    RouteMode Mode = RouteMode.Rewrite);

/// <summary>
/// One s2s tunnel. Bind 0.0.0.0:port for inbound (other pods → my pod), bind
/// 127.0.0.1:port for outbound (local exe → peer pod). Upstream is resolved
/// fresh per connection so peer-pod IP churn doesn't need a proxy restart.
/// </summary>
public sealed record S2sRoute(System.Net.IPAddress BindAddress, int ListenPort, string UpstreamHost, int UpstreamPort)
{
    public bool IsInbound => BindAddress.Equals(System.Net.IPAddress.Any) || BindAddress.Equals(System.Net.IPAddress.IPv6Any);
}
