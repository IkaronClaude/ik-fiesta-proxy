using System.Net;
using System.Net.Sockets;
using FiestaProxy.Config;

namespace FiestaProxy.Net;

/// <summary>
/// Accepts player connections on one listen port and spawns a ProxySession per
/// connection. One ProxyListener per ProxyRoute; lifetime is the process.
/// </summary>
public sealed class ProxyListener
{
    private readonly ProxyRoute _route;
    private readonly ProxyConfig _config;

    public ProxyListener(ProxyRoute route, ProxyConfig config)
    {
        _route = route;
        _config = config;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Stay unbound until the upstream exe is reachable -- players get a
        // clean "connection refused" while the server is still booting
        // instead of a connect-then-drop. See Upstream.
        await Upstream.WaitUntilReachableAsync(
            _route.UpstreamHost, _route.UpstreamPort, _config.UpstreamConnectTimeout,
            $"[{_route.ServiceName}]", ct);
        if (ct.IsCancellationRequested) return;

        var listener = new TcpListener(IPAddress.Any, _route.ListenPort);
        listener.Start();
        Log.Info($"listen :{_route.ListenPort} -> {_route.UpstreamHost}:{_route.UpstreamPort} ({_route.ServiceName}) -- listening");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => RunSessionAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var session = new ProxySession(client, _route, _config);
            await session.RunAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{_route.ServiceName}] session error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
