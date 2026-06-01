using System.Net.Sockets;
using FiestaLibReloaded.Networking;
using FiestaProxy.Config;
using FiestaProxy.Crypto;
using FiestaProxy.Rewrites;

namespace FiestaProxy.Net;

/// <summary>
/// One accepted player connection.
///
/// Traffic model (per Ikaron/fiesta-filter analysis):
///   client -> server : XOR-encrypted after a handshake notification.
///   server -> client : plaintext on the wire.
///
/// The S→C direction on a <see cref="RouteMode.Rewrite"/> route goes through
/// <see cref="PumpAsync"/> — FiestaConnection-framed so the rewriter registry
/// can mutate IP/port fields and re-emit. Everywhere else (C→S on any route,
/// and both directions of an opaque route) uses <see cref="PumpFramedRawAsync"/>,
/// which still reads frames so each packet can be logged but passes the
/// ORIGINAL wire bytes through untouched — no re-encoding, byte-perfect.
///
/// The XOR cipher is used only for the log line: when the S→C SEED_ACK
/// (opcode 0x0807) is observed we initialise <see cref="_c2sCipher"/>, and the
/// C→S pump decrypts each body INTO A COPY before logging so the operator
/// sees the actual opcode/payload. The wire bytes forwarded to the upstream
/// stay encrypted exactly as the client sent them.
/// </summary>
public sealed class ProxySession
{
    private readonly TcpClient _client;
    private readonly ProxyRoute _route;
    private readonly ProxyConfig _config;
    private readonly PacketRewriterRegistry _rewriters;

    /// <summary>Initialised on the first S→C SEED_ACK (0x0807) — used by the
    /// C→S raw pump to decrypt bodies for the log line. Null until then or
    /// when no BYO XOR table was configured.</summary>
    private FiestaXorCipher? _c2sCipher;

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
            // The listener was health-gated, so the upstream was reachable at
            // bind time; a single dial suffices. On the rare miss, close the
            // client — it reconnects. No holding/retry (that piles up).
            upstream = await Upstream.DialAsync(
                _route.UpstreamHost, _route.UpstreamPort, _config.UpstreamConnectTimeout, ct);
            if (upstream is null)
            {
                Log.Warn($"[{_route.ServiceName}] upstream {_route.UpstreamHost}:{_route.UpstreamPort} unreachable — closing client");
                return;
            }

            // Nagle stalls small Fiesta packets behind the 40ms ACK timer — kill it
            // on both legs so opcodes ship immediately instead of pooling.
            _client.NoDelay = true;
            upstream.NoDelay = true;

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clientStream = _client.GetStream();
            var upstreamStream = upstream.GetStream();
            var upstreamEp = $"{_route.UpstreamHost}:{_route.UpstreamPort}";

            if (_route.Mode == RouteMode.Opaque)
            {
                // Opaque route (in-game Zone channel): no rewriter ever
                // touches a packet, so both directions use the raw framed
                // pump — packets are read, logged, and passed through wire-
                // intact. C→S bodies are decrypted for the log only.
                var up   = PumpFramedRawAsync(clientStream, upstreamStream,
                    $"[{_route.ServiceName}] opaque C->S {clientEp}",
                    isFromClient: true, sessionCts.Token);
                var down = PumpFramedRawAsync(upstreamStream, clientStream,
                    $"[{_route.ServiceName}] opaque S->C {upstreamEp}",
                    isFromClient: false, sessionCts.Token);
                await Task.WhenAny(up, down);
            }
            else
            {
                // Rewrite route. S→C goes through the framed pump (the
                // rewriter registry may resize/modify packets, so re-encoding
                // via FiestaConnection is required). C→S goes through the
                // framed RAW pump — every frame is read for logging but the
                // wire bytes are passed through unchanged, so the client's
                // XOR-encrypted body reaches the server exactly as sent.
                var upstreamConn = new FiestaConnection(upstreamStream, NullCipher.Instance);
                var clientConn   = new FiestaConnection(clientStream, NullCipher.Instance);

                var c2s = PumpFramedRawAsync(clientStream, upstreamStream,
                    $"[{_route.ServiceName}] C->S {clientEp}",
                    isFromClient: true, sessionCts.Token);
                var s2c = PumpAsync(upstreamConn, clientConn, isFromClient: false, sessionCts.Token);

                await Task.WhenAny(c2s, s2c);
            }
        }
        finally
        {
            try { _client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
            Log.Info($"[{_route.ServiceName}] close {clientEp}");
        }
    }

    /// <summary>
    /// Framed pump with rewriter dispatch — used for the S→C side of a
    /// <see cref="RouteMode.Rewrite"/> route, where the rewriter registry may
    /// modify packets. Also watches for the S→C SEED_ACK (0x0807) so the C→S
    /// raw pump can decrypt-for-log thereafter.
    /// </summary>
    private async Task PumpAsync(FiestaConnection from, FiestaConnection to, bool isFromClient, CancellationToken ct)
    {
        var peerLabel = isFromClient
            ? _client.Client.RemoteEndPoint?.ToString() ?? "?"
            : $"{_route.UpstreamHost}:{_route.UpstreamPort}";
        var arrow = isFromClient ? "C->S" : "S->C";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pkt = await from.ReadPacketAsync(ct);

                // Catch SEED_ACK on the S→C side so the C→S raw pump can
                // decrypt bodies for its log lines. Frame layout for SEED:
                // opcode 0x0807 + 2-byte LE seed in the payload.
                if (!isFromClient
                    && pkt.Opcode == 0x0807
                    && pkt.Payload.Length >= 2
                    && _c2sCipher is null
                    && _config.XorTable is not null)
                {
                    var seed = (ushort)(pkt.Payload.Span[0] | (pkt.Payload.Span[1] << 8));
                    _c2sCipher = new FiestaXorCipher(_config.XorTable, seed);
                    PacketLog.Info($"[{_route.ServiceName}] SEED_ACK observed — c2s cipher armed (seed=0x{seed:X4})");
                }

                PacketLog.Info($"[{_route.ServiceName}] {arrow} {peerLabel}: opcode=0x{pkt.Opcode:X4} payload_len={pkt.Payload.Length}  {PacketLog.Hex(pkt.Payload)}");
                if (!isFromClient)
                {
                    var rewritten = _rewriters.Apply(pkt);
                    if (!ReferenceEquals(rewritten, pkt))
                    {
                        PacketLog.Info($"[{_route.ServiceName}] {arrow} {peerLabel} [rewritten]: opcode=0x{rewritten.Opcode:X4} payload_len={rewritten.Payload.Length}  {PacketLog.Hex(rewritten.Payload)}");
                        pkt = rewritten;
                    }
                }
                await to.WritePacketAsync(pkt, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { /* peer closed */ }
        catch (IOException) { /* peer closed */ }
        catch (ObjectDisposedException) { /* socket force-closed by other pump */ }
    }

    /// <summary>
    /// Packet-aware passthrough pump. Reads one Fiesta frame at a time
    /// (length prefix + body), logs opcode + payload preview (decrypting the
    /// body via <see cref="_c2sCipher"/> for C→S once SEED is known), and
    /// writes the EXACT original wire bytes to the destination. No re-encoding,
    /// no rewriting — byte-perfect passthrough.
    /// </summary>
    private async Task PumpFramedRawAsync(NetworkStream from, NetworkStream to, string label, bool isFromClient, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFiestaFrameAsync(from, ct);
                if (frame is null) return;
                var (wire, bodyOffset, bodyLen) = frame.Value;

                // Decrypt a COPY of the body just for logging. The wire
                // buffer stays untouched and is forwarded below as-is.
                var bodyCopy = wire.AsSpan(bodyOffset, bodyLen).ToArray();
                if (isFromClient && _c2sCipher is { } cipher)
                    cipher.Transform(bodyCopy);

                var opcode = bodyLen >= 2 ? (ushort)(bodyCopy[0] | (bodyCopy[1] << 8)) : (ushort)0;
                var payloadLen = Math.Max(0, bodyLen - 2);
                var payloadMem = bodyLen >= 2
                    ? new ReadOnlyMemory<byte>(bodyCopy, 2, payloadLen)
                    : new ReadOnlyMemory<byte>(bodyCopy);
                PacketLog.Info($"{label}: opcode=0x{opcode:X4} payload_len={payloadLen}  {PacketLog.Hex(payloadMem)}");

                // Catch the S→C SEED_ACK from the raw side too (opaque
                // routes only get the raw pump on S→C).
                if (!isFromClient
                    && opcode == 0x0807
                    && bodyLen >= 4
                    && _c2sCipher is null
                    && _config.XorTable is not null)
                {
                    var seed = (ushort)(bodyCopy[2] | (bodyCopy[3] << 8));
                    _c2sCipher = new FiestaXorCipher(_config.XorTable, seed);
                    PacketLog.Info($"[{_route.ServiceName}] SEED_ACK observed (raw) — c2s cipher armed (seed=0x{seed:X4})");
                }

                await to.WriteAsync(wire, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* peer closed */ }
        catch (ObjectDisposedException) { /* socket force-closed by other pump */ }
    }

    /// <summary>
    /// Read one complete Fiesta frame from the network. Returns the full wire
    /// bytes (length prefix + body), the offset where the body starts, and
    /// the body length. Returns null on EOF or malformed framing.
    ///
    /// Framing:
    ///   * 1-byte inline length (1..255): wire = [len] + body
    ///   * 3-byte extended    (0x00 + LE u16): wire = [00, lo, hi] + body
    /// </summary>
    private static async Task<(byte[] Wire, int BodyOffset, int BodyLen)?> ReadFiestaFrameAsync(NetworkStream s, CancellationToken ct)
    {
        var first = new byte[1];
        if (!await ReadExactlyAsync(s, first.AsMemory(), ct)) return null;

        int bodyLen, prefixLen;
        byte[] wire;
        if (first[0] != 0x00)
        {
            bodyLen = first[0];
            prefixLen = 1;
            wire = new byte[1 + bodyLen];
            wire[0] = first[0];
        }
        else
        {
            var ext = new byte[2];
            if (!await ReadExactlyAsync(s, ext.AsMemory(), ct)) return null;
            bodyLen = ext[0] | (ext[1] << 8);
            prefixLen = 3;
            wire = new byte[3 + bodyLen];
            wire[0] = 0x00;
            wire[1] = ext[0];
            wire[2] = ext[1];
        }
        if (bodyLen < 2) return null; // malformed — body must include 2-byte opcode
        if (!await ReadExactlyAsync(s, wire.AsMemory(prefixLen, bodyLen), ct)) return null;
        return (wire, prefixLen, bodyLen);
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream s, Memory<byte> buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            int n;
            try { n = await s.ReadAsync(buf.Slice(off), ct); }
            catch { return false; }
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
