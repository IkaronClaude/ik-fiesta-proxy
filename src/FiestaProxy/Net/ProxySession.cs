using System.Net.Sockets;
using FiestaLibReloaded.Networking;
using FiestaProxy.Config;
using FiestaProxy.Rewrites;

namespace FiestaProxy.Net;

/// <summary>
/// One accepted player connection.
///
/// Traffic model (per Ikaron/fiesta-filter analysis):
///   client -> server : XOR-encrypted after a handshake notification. The proxy
///                      pumps these bytes opaque: it never inspects C→S content
///                      so encrypted bytes pass through byte-for-byte and the
///                      cipher is irrelevant to the hot path.
///   server -> client : plaintext on the wire. FiestaConnection (NullCipher)
///                      reads frames, the rewriter registry patches IP+port
///                      fields, and the rewritten frame is written back to
///                      the client.
///
/// FiestaXorCipher (lifted from fiesta-filter) is wired in for symmetry but
/// stays dormant unless future hooks need to read C→S traffic.
/// </summary>
public sealed class ProxySession
{
    private readonly TcpClient _client;
    private readonly ProxyRoute _route;
    private readonly ProxyConfig _config;
    private readonly PacketRewriterRegistry _rewriters;

    public ProxySession(TcpClient client, ProxyRoute route, ProxyConfig config)
    {
        _client = client;
        _route = route;
        _config = config;
        _rewriters = PacketRewriterRegistry.Default(config);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var clientEp = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"[{_route.ServiceName}] accept {clientEp}");

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
                // Upstream refused → close the client side so they don't wait
                // for a handshake that's never coming. Without this the client
                // sees a TCP-open but no protocol bytes flowing, and only
                // their app-layer timeout (often minutes) eventually catches
                // the wedge.
                Log.Warn($"[{_route.ServiceName}] upstream {_route.UpstreamHost}:{_route.UpstreamPort} unreachable ({ex.GetType().Name}: {ex.Message})");
                return;
            }

            // Nagle stalls small Fiesta packets behind the 40ms ACK timer — kill it
            // on both legs so opcodes ship immediately instead of pooling.
            _client.NoDelay = true;
            upstream.NoDelay = true;

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clientStream = _client.GetStream();
            var upstreamStream = upstream.GetStream();

            // S→C is plaintext on the wire — NullCipher is correct here.
            var upstreamConn = new FiestaConnection(upstreamStream, NullCipher.Instance);
            var clientConn = new FiestaConnection(clientStream, NullCipher.Instance);

            var c2s = PumpRawAsync(clientStream, upstreamStream, sessionCts.Token);
            var s2c = PumpServerToClientAsync(upstreamConn, clientConn, sessionCts.Token);

            // Let pumps run to natural completion. When one peer closes (FIN),
            // their pump returns EOF; the OTHER side will eventually see its
            // peer close too (the close cascades through the TCP stack and
            // app-layer code on both sides) and that pump also returns. No
            // need to force-close from here — that causes RST on Windows and
            // peers suppress in-flight bytes (a server-sent WORLDSELECT_ACK
            // still in the receive buffer gets discarded on RST).
            await Task.WhenAll(c2s, s2c);
        }
        finally
        {
            // Always close client side, even on upstream-connect failure.
            try { _client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
            Log.Info($"[{_route.ServiceName}] close {clientEp}");
        }
    }

    /// <summary>Plaintext byte pump. Client → server traffic is unencrypted.</summary>
    private static async Task PumpRawAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
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

    /// <summary>Framed pump. Decrypts, rewrites, re-encrypts.</summary>
    private async Task PumpServerToClientAsync(FiestaConnection upstream, FiestaConnection client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pkt = await upstream.ReadPacketAsync(ct);
                var rewritten = _rewriters.Apply(pkt);
                await client.WritePacketAsync(rewritten, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { /* peer closed */ }
        catch (IOException) { /* peer closed */ }
        catch (ObjectDisposedException) { /* socket force-closed by other pump */ }
    }
}
