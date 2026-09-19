using System.Net;
using System.Net.Sockets;
using FiestaProxy.Config;
using FiestaProxy.Plugins;

namespace FiestaProxy.Net;

/// <summary>
/// Accepts player connections on one listen port and spawns a ProxySession per
/// connection. One ProxyListener per ProxyRoute; lifetime is the process.
/// </summary>
public sealed class ProxyListener
{
    private readonly ProxyRoute _route;
    private readonly ProxyConfig _config;
    private readonly PluginHost _plugins;

    public ProxyListener(ProxyRoute route, ProxyConfig config, PluginHost? plugins = null)
    {
        _route = route;
        _config = config;
        _plugins = plugins ?? new PluginHost();
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

        // Retry the BIND, and say so. This used to be a bare Start(): when it threw, the exception went into
        // this task, and Program awaits the listeners with Task.WhenAll - which only surfaces a fault once
        // every task has finished, and the other listeners run for ever. So a failed bind was never logged
        // and the listener simply never existed. Seen 2026-09-19: Zone_0_4 logged "opening listener", never
        // "listening", and :19028 stayed closed although the port was free moments later - every map on
        // zone 4 would have hung the client at 0% on the transition.
        TcpListener listener;
        for (var attempt = 1; ; attempt++)
        {
            listener = new TcpListener(IPAddress.Any, _route.ListenPort);
            try
            {
                listener.Start();
                break;
            }
            catch (SocketException e) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"[{_route.ServiceName}] cannot bind :{_route.ListenPort} ({e.SocketErrorCode}: {e.Message}) " +
                         $"- attempt {attempt}, retrying in 2s");
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
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
            var session = new ProxySession(client, _route, _config, _plugins);
            await session.RunAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{_route.ServiceName}] session error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
