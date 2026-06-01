using System.Net;
using System.Net.Sockets;
using FiestaProxy.Config;

namespace FiestaProxy.Net;

/// <summary>
/// One s2s tunnel listener.
///
/// Outbound routes (bind 127.0.0.1) stay closed until a marker health probe
/// confirms the peer's exe is ready — so the local exe's s2s "friendly check"
/// can't succeed early. Inbound routes (bind 0.0.0.0) listen immediately and
/// answer marker probes with this pod's exe-readiness; only real (non-marker)
/// peer connections cause the inbound proxy to dial its own exe.
///
/// The one "early conn" to a Fiesta exe is this listener's own background
/// probe of its co-located exe (used solely to set the readiness flag returned
/// in marker ACKs). Every other readiness check is proxy-to-proxy via the
/// marker opcodes and never touches an exe.
/// </summary>
public sealed class S2sListener
{
    private readonly S2sRoute _route;
    private readonly IReadOnlyList<IPNetwork> _allowedCidrs;
    private readonly TimeSpan _upstreamConnectTimeout;
    private volatile bool _exeReady;

    public S2sListener(S2sRoute route, IReadOnlyList<IPNetwork> allowedCidrs, TimeSpan upstreamConnectTimeout)
    {
        _route = route;
        _allowedCidrs = allowedCidrs;
        _upstreamConnectTimeout = upstreamConnectTimeout;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var dir = _route.IsInbound ? "inbound" : "outbound";

        if (_route.IsInbound)
        {
            // Track our exe's readiness once, in the background. This is the
            // ONLY connection the health path makes to a Fiesta exe — peers'
            // marker probes are answered with this flag, never by dialing the
            // exe.
            _ = Task.Run(() => TrackExeReadinessAsync(ct), ct);
        }
        else
        {
            // Stay unbound until the peer answers a marker REQ with "ready".
            await S2sHealthCheck.WaitUntilPeerHealthyAsync(
                _route.UpstreamHost, _route.UpstreamPort, _upstreamConnectTimeout,
                $"s2s outbound :{_route.ListenPort}", ct);
            if (ct.IsCancellationRequested) return;
        }

        var listener = new TcpListener(_route.BindAddress, _route.ListenPort);
        listener.Start();
        Log.Info($"s2s {dir,-8} {_route.BindAddress}:{_route.ListenPort} -> {_route.UpstreamHost}:{_route.UpstreamPort}  (listening)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => RunSessionAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task TrackExeReadinessAsync(CancellationToken ct)
    {
        await Upstream.WaitUntilReachableAsync(
            _route.UpstreamHost, _route.UpstreamPort, _upstreamConnectTimeout,
            $"s2s inbound :{_route.ListenPort} exe-probe", ct);
        if (!ct.IsCancellationRequested) _exeReady = true;
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
            var session = new S2sSession(client, _route, _upstreamConnectTimeout,
                _route.IsInbound ? () => _exeReady : null);
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
