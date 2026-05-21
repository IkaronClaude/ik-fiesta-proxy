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
    public required string PublicIp { get; init; }

    public static ProxyConfig FromEnvironment()
    {
        var publicIp = Environment.GetEnvironmentVariable("PUBLIC_IP")
            ?? throw new InvalidOperationException("PUBLIC_IP env var is required");

        var routesEnv = Environment.GetEnvironmentVariable("PROXY_ROUTES")
            ?? throw new InvalidOperationException("PROXY_ROUTES env var is required");

        var routes = new List<ProxyRoute>();
        foreach (var entry in routesEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 4)
                throw new InvalidOperationException($"PROXY_ROUTES entry malformed (expect 4 colon-separated fields): '{entry}'");
            if (!int.TryParse(parts[0], out var listen) || !int.TryParse(parts[3], out var upstreamPort))
                throw new InvalidOperationException($"PROXY_ROUTES entry has non-numeric port: '{entry}'");
            routes.Add(new ProxyRoute(listen, parts[1], parts[2], upstreamPort));
        }

        if (routes.Count == 0)
            throw new InvalidOperationException("PROXY_ROUTES yielded zero routes");

        return new ProxyConfig { Routes = routes, PublicIp = publicIp };
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
