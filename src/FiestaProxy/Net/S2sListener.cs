using System.Net;
using System.Net.Sockets;
using FiestaProxy.Config;

namespace FiestaProxy.Net;

/// <summary>
/// One s2s tunnel listener. Accepts on the configured bind:port and starts an
/// S2sSession per connection. Inbound listeners (bound 0.0.0.0) enforce the
/// S2S_ALLOWED_CIDRS source-IP allow-list before promoting the connection to
/// a session. Outbound listeners (bound 127.0.0.1) skip the check since the
/// kernel already restricts the source to loopback.
/// </summary>
public sealed class S2sListener
{
    private readonly S2sRoute _route;
    private readonly IReadOnlyList<IPNetwork> _allowedCidrs;

    public S2sListener(S2sRoute route, IReadOnlyList<IPNetwork> allowedCidrs)
    {
        _route = route;
        _allowedCidrs = allowedCidrs;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_route.BindAddress, _route.ListenPort);
        listener.Start();
        var dir = _route.IsInbound ? "inbound" : "outbound";
        Log.Info($"s2s {dir,-8} {_route.BindAddress}:{_route.ListenPort} -> {_route.UpstreamHost}:{_route.UpstreamPort}");

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
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (_route.IsInbound && remote is not null && !IsAllowed(remote.Address))
        {
            Log.Warn($"s2s reject {remote.Address} -> :{_route.ListenPort} (not in S2S_ALLOWED_CIDRS)");
            try { client.Close(); } catch { }
            return;
        }

        try
        {
            var session = new S2sSession(client, _route);
            await session.RunAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"s2s session error :{_route.ListenPort}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsAllowed(IPAddress addr)
    {
        foreach (var net in _allowedCidrs)
            if (net.Contains(addr)) return true;
        return false;
    }
}
