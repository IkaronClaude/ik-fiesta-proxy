using System.Net.Sockets;
using FiestaProxy.Config;

namespace FiestaProxy.Net;

/// <summary>
/// One s2s tunnel session: pure bidirectional byte pump. No framing parse, no
/// cipher, no rewriters — s2s uses the OPTool format (unencrypted) and we
/// never inspect content. Same force-close-on-disconnect + NoDelay discipline
/// as ProxySession so peer-side disconnects propagate within milliseconds.
///
/// Upstream is resolved fresh on every connect — peer pod IP churn doesn't
/// require a proxy restart.
/// </summary>
public sealed class S2sSession
{
    private readonly TcpClient _peer;
    private readonly S2sRoute _route;

    public S2sSession(TcpClient peer, S2sRoute route)
    {
        _peer = peer;
        _route = route;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var peerEp = _peer.Client.RemoteEndPoint?.ToString() ?? "?";
        var dir = _route.IsInbound ? "inbound" : "outbound";
        Log.Info($"s2s {dir} accept {peerEp} :{_route.ListenPort}");

        TcpClient? upstream = null;
        try
        {
            upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(_route.UpstreamHost, _route.UpstreamPort, ct);
            }
            catch (Exception ex)
            {
                // Upstream refused / unreachable. We MUST close _peer here —
                // otherwise the exe sits with a half-open connection waiting
                // for protocol bytes that never arrive, and only its app-layer
                // heartbeat (often 3+ minutes) eventually catches the wedge.
                // The outer finally handles that close; just log here.
                Log.Warn($"s2s {dir} upstream {_route.UpstreamHost}:{_route.UpstreamPort} unreachable ({ex.GetType().Name}: {ex.Message})");
                return;
            }

            _peer.NoDelay = true;
            upstream.NoDelay = true;

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var peerStream = _peer.GetStream();
            var upStream = upstream.GetStream();

            var ab = PumpAsync(peerStream, upStream, sessionCts.Token);
            var ba = PumpAsync(upStream, peerStream, sessionCts.Token);

            try { await Task.WhenAny(ab, ba); }
            finally
            {
                sessionCts.Cancel();
                // Half-close (Shutdown) instead of Close — wakes the blocked
                // Read on the other pump with EOF rather than RST. RST trips
                // peers into "abnormal termination" handling and they may
                // suppress in-flight bytes. Final Close() runs after pumps drain.
                try { _peer.Client.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
                try { upstream.Client.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
                try { await Task.WhenAll(ab, ba); } catch { /* swallow */ }
            }
        }
        finally
        {
            // Always close the inbound side, even on upstream-connect failure
            // or any other short-circuit path. Without this the exe's
            // connection dangles until its heartbeat timeout fires.
            try { _peer.Close(); } catch { }
            try { upstream?.Close(); } catch { }
            Log.Info($"s2s {dir} close  {peerEp} :{_route.ListenPort}");
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buf, ct);
                if (n <= 0) return;
                await to.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* peer closed */ }
        catch (ObjectDisposedException) { /* socket force-closed by other pump */ }
    }
}
