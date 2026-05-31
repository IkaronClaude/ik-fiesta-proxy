using System.Net.Sockets;
using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Net;

/// <summary>
/// One s2s tunnel session.
///
/// s2s = OPTool format, unencrypted both directions. We frame with
/// FiestaConnection + NullCipher and log every packet so an operator can
/// cross-reference opcodes with PDB-derived layouts in FiestaLib-Reloaded.
///
/// Inbound sessions inspect the opening packet: if it carries the proxy's
/// health-check opcode we answer with an ACK and close — WITHOUT dialing our
/// exe. Otherwise we treat it as real peer traffic, forward the first packet
/// to the exe (we already consumed it from the wire), and pump from there.
/// </summary>
public sealed class S2sSession
{
    private readonly TcpClient _peer;
    private readonly S2sRoute _route;
    private readonly TimeSpan _upstreamConnectTimeout;
    private readonly Func<bool>? _exeReady;

    public S2sSession(TcpClient peer, S2sRoute route, TimeSpan upstreamConnectTimeout, Func<bool>? exeReady)
    {
        _peer = peer;
        _route = route;
        _upstreamConnectTimeout = upstreamConnectTimeout;
        _exeReady = exeReady;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_route.IsInbound) await RunInboundAsync(ct);
        else await RunOutboundAsync(ct);
    }

    private async Task RunOutboundAsync(CancellationToken ct)
    {
        var peerEp = _peer.Client.RemoteEndPoint?.ToString() ?? "?";
        PacketLog.Info($"s2s outbound accept {peerEp} :{_route.ListenPort}");
        TcpClient? upstream = null;
        try
        {
            upstream = await Upstream.DialAsync(
                _route.UpstreamHost, _route.UpstreamPort, _upstreamConnectTimeout, ct);
            if (upstream is null)
            {
                Log.Warn($"s2s outbound upstream {_route.UpstreamHost}:{_route.UpstreamPort} unreachable -- closing peer");
                return;
            }
            await ForwardAsync(upstream, firstFromPeer: null, peerConnPrebuilt: null, ct);
        }
        finally
        {
            try { _peer.Close(); } catch { }
            try { upstream?.Close(); } catch { }
            PacketLog.Info($"s2s outbound close  {peerEp} :{_route.ListenPort}");
        }
    }

    private async Task RunInboundAsync(CancellationToken ct)
    {
        var peerEp = _peer.Client.RemoteEndPoint?.ToString() ?? "?";
        TcpClient? upstream = null;
        try
        {
            _peer.NoDelay = true;
            var peerConn = new FiestaConnection(_peer.GetStream(), NullCipher.Instance);

            // Classify the connection with a SHORT window. Markers arrive in
            // milliseconds (the peer's gate writes the REQ immediately on
            // connect). Real connections come in two flavours:
            //   * client-speaks-first (peer registers right away) — the first
            //     packet also arrives in milliseconds, and we read it here.
            //   * server-speaks-first (the local exe sends a greeting first)
            //     — no packet arrives within the window, we fall through to
            //     dialing the exe with firstFromPeer=null and let the pumps
            //     handle the bidirectional flow.
            // 300ms covers loopback/LAN latency while staying short enough
            // that server-speaks-first connections feel snappy.
            FiestaPacket? first = null;
            try
            {
                using var classifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                classifyCts.CancelAfter(TimeSpan.FromMilliseconds(300));
                first = await peerConn.ReadPacketAsync(classifyCts.Token);
            }
            catch
            {
                first = null;
            }

            if (first is { } f && f.Opcode == S2sHealthCheck.OpcodeReq)
            {
                // Health check from a peer's outbound gate. Answer with our
                // exe's readiness and close. We do NOT dial the exe — that's
                // the whole point of the marker protocol.
                var ready = _exeReady?.Invoke() ?? false;
                var ackPayload = new[] {
                    ready ? S2sHealthCheck.StatusReady : S2sHealthCheck.StatusNotReady
                };
                try
                {
                    await peerConn.WritePacketAsync(
                        new FiestaPacket(S2sHealthCheck.OpcodeAck, ackPayload), ct);
                }
                catch { }
                return;
            }

            // Real s2s connection. Dial the exe ONLY now (the marker never
            // got dialed, that's the point). If we did read a first packet,
            // replay it to the upstream before the pumps start.
            PacketLog.Info($"s2s inbound  accept {peerEp} :{_route.ListenPort}  (first-packet={(first is null ? "<server-speaks-first>" : $"0x{first.Opcode:X4}")})");
            upstream = await Upstream.DialAsync(
                _route.UpstreamHost, _route.UpstreamPort, _upstreamConnectTimeout, ct);
            if (upstream is null)
            {
                Log.Warn($"s2s inbound upstream {_route.UpstreamHost}:{_route.UpstreamPort} unreachable -- closing peer");
                return;
            }
            await ForwardAsync(upstream, firstFromPeer: first, peerConnPrebuilt: peerConn, ct);
        }
        finally
        {
            try { _peer.Close(); } catch { }
            try { upstream?.Close(); } catch { }
            if (upstream is not null)
                PacketLog.Info($"s2s inbound  close  {peerEp} :{_route.ListenPort}");
        }
    }

    /// <summary>Set up framed pumps in both directions. If
    /// <paramref name="firstFromPeer"/> is non-null it was already consumed
    /// from peerConn during the marker-classify step; it is written to the
    /// upstream before the peer→upstream pump starts.</summary>
    private async Task ForwardAsync(TcpClient upstream, FiestaPacket? firstFromPeer, FiestaConnection? peerConnPrebuilt, CancellationToken ct)
    {
        _peer.NoDelay = true;
        upstream.NoDelay = true;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var peerConn = peerConnPrebuilt ?? new FiestaConnection(_peer.GetStream(), NullCipher.Instance);
        var upConn = new FiestaConnection(upstream.GetStream(), NullCipher.Instance);

        var peerLabel = _peer.Client.RemoteEndPoint?.ToString() ?? "?";
        var upLabel = $"{_route.UpstreamHost}:{_route.UpstreamPort}";
        var dir = _route.IsInbound ? "in " : "out";

        if (firstFromPeer is { } first)
        {
            PacketLog.Info($"s2s {dir} {peerLabel} -> {upLabel}: opcode=0x{first.Opcode:X4} payload_len={first.Payload.Length}  {PacketLog.Hex(first.Payload)}");
            await upConn.WritePacketAsync(first, sessionCts.Token);
        }

        var ab = PumpFramedAsync(peerConn, upConn, $"s2s {dir} {peerLabel} -> {upLabel}", sessionCts.Token);
        var ba = PumpFramedAsync(upConn, peerConn, $"s2s {dir} {upLabel} -> {peerLabel}", sessionCts.Token);
        await Task.WhenAny(ab, ba);
    }

    private static async Task PumpFramedAsync(FiestaConnection from, FiestaConnection to, string label, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pkt = await from.ReadPacketAsync(ct);
                PacketLog.Info($"{label}: opcode=0x{pkt.Opcode:X4} payload_len={pkt.Payload.Length}  {PacketLog.Hex(pkt.Payload)}");
                await to.WritePacketAsync(pkt, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
