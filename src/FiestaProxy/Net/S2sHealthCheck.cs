using System.Net.Sockets;
using FiestaLibReloaded.Networking;

namespace FiestaProxy.Net;

/// <summary>
/// Proxy-to-proxy s2s readiness check, using a Fiesta-framed packet with two
/// opcodes reserved for the proxy (no Fiesta exe ever sends them):
///
///   outbound gate ─►  PROXY_HEALTHCHK_REQ                ─►  peer inbound proxy
///   outbound gate ◄─  PROXY_HEALTHCHK_ACK[status:1]      ◄─  peer inbound proxy
///
/// The peer's inbound proxy handles the request entirely at the proxy layer —
/// it never dials its own exe — so a readiness probe never lands an early,
/// dataless connection on a Fiesta exe (which otherwise triggers the exe's
/// "first connect = friendly check → spin up the whole connection pool" boot
/// path against a not-yet-ready peer).
/// </summary>
internal static class S2sHealthCheck
{
    /// <summary>Proxy-internal opcode for the health-check request (no payload).</summary>
    public const ushort OpcodeReq = 0xFEED;

    /// <summary>Proxy-internal opcode for the health-check ack. One-byte
    /// payload: 1 = our exe is ready, 0 = not yet.</summary>
    public const ushort OpcodeAck = 0xFEEE;

    public const byte StatusReady = 0x01;
    public const byte StatusNotReady = 0x00;

    /// <summary>True if <paramref name="opcode"/> belongs to the proxy's
    /// internal health-check protocol (peers never emit these).</summary>
    public static bool IsProxyOpcode(ushort opcode) => opcode == OpcodeReq || opcode == OpcodeAck;

    /// <summary>
    /// Block until the peer's inbound proxy answers a REQ with an ACK whose
    /// status byte is "exe ready". Retries every second; returns early only
    /// on cancel.
    /// </summary>
    public static async Task WaitUntilPeerHealthyAsync(
        string host, int port, TimeSpan attemptTimeout, string logTag, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (await ProbeAsync(host, port, attemptTimeout, ct) == StatusReady)
            {
                Log.Info($"{logTag} peer {host}:{port} healthy after {attempt} probe(s) — opening listener");
                return;
            }
            if (attempt == 1)
                Log.Info($"{logTag} peer {host}:{port} not ready yet — holding listener closed");
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<byte> ProbeAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            var conn = new FiestaConnection(client.GetStream(), NullCipher.Instance);
            await conn.WritePacketAsync(new FiestaPacket(OpcodeReq, Array.Empty<byte>()), cts.Token);
            var reply = await conn.ReadPacketAsync(cts.Token);
            if (reply.Opcode == OpcodeAck && reply.Payload.Length >= 1)
                return reply.Payload.Span[0];
            return StatusNotReady;
        }
        catch (Exception)
        {
            return StatusNotReady;
        }
    }
}
