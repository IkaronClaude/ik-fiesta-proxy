using System.Net.Sockets;
using FiestaLibReloaded.Networking;
using FiestaProxy.Config;
using FiestaProxy.Crypto;
using FiestaProxy.Plugins;

namespace FiestaProxy.Net;

/// <summary>
/// The pump for a <see cref="RouteMode.Bridge"/> route: BOTH directions framed, decoded and
/// re-emitted, with plugin sessions allowed to rewrite, drop or answer packets.
///
/// This is what the rewrite route cannot do. There, C→S is passed through byte-perfect and the
/// cipher is only used to decrypt a copy for the log. A bridge has to change what the client sends
/// before the server sees it, so it decrypts, hands the plaintext to the plugins, and re-encrypts
/// whatever comes back.
///
/// Two cipher instances, one seed
/// -----------------------------
/// The C→S cipher is a stream: its position advances one step per byte. The bridge therefore keeps
/// TWO instances from the same seed. <c>_decrypt</c> consumes exactly what the client sent, so it
/// stays in step with the client. <c>_encrypt</c> consumes exactly what we send on, so it stays in
/// step with the server. They deliberately diverge whenever a translation changes a packet's length
/// (a 2026 login is 349 bytes and its 2016 form is 316), and that is correct: each end only ever
/// sees its own side of the conversation.
///
/// Write ordering
/// --------------
/// Either pump may write in either direction, because answering a handshake locally means the C→S
/// pump sends to the client. Each direction therefore has its own lock, so two frames can never
/// interleave on one socket.
/// </summary>
internal sealed class BridgePump
{
    private readonly NetworkStream _clientStream;
    private readonly NetworkStream _serverStream;
    private readonly ProxyConfig _config;
    private readonly string _service;
    private readonly IReadOnlyList<IPluginSession> _sessions;

    private readonly SemaphoreSlim _clientWrite = new(1, 1);
    private readonly SemaphoreSlim _serverWrite = new(1, 1);

    private FiestaXorCipher? _decrypt;   // what the client sent -> plaintext
    private FiestaXorCipher? _encrypt;   // what we send on -> what the server expects

    public BridgePump(NetworkStream clientStream, NetworkStream serverStream, ProxyConfig config,
                      string service, IReadOnlyList<IPluginSession> sessions)
    {
        _clientStream = clientStream;
        _serverStream = serverStream;
        _config = config;
        _service = service;
        _sessions = sessions;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var c2s = PumpAsync(fromClient: true, ct);
        var s2c = PumpAsync(fromClient: false, ct);
        await Task.WhenAny(c2s, s2c);
    }

    private async Task PumpAsync(bool fromClient, CancellationToken ct)
    {
        var from = fromClient ? _clientStream : _serverStream;
        var arrow = fromClient ? "C->S" : "S->C";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await FiestaFraming.ReadAsync(from, ct);
                if (frame is null) return;
                var body = frame.Value.Body;

                if (fromClient)
                {
                    if (_decrypt is null)
                    {
                        // Nothing can be read before the seed arrives. Pass it on untouched rather
                        // than guessing: the server will reject it either way, and mangling it would
                        // hide the real problem.
                        await WriteRawToServerAsync(frame.Value.Wire, ct);
                        continue;
                    }
                    _decrypt.Transform(body);
                }
                else
                {
                    ArmCiphersIfSeed(body);
                }

                var opcode = (ushort)(body[0] | (body[1] << 8));
                var packet = new FiestaPacket(opcode, new ReadOnlyMemory<byte>(body, 2, body.Length - 2));

                var ctx = new PluginPacketContext(packet, fromClient);
                foreach (var s in _sessions)
                {
                    try
                    {
                        if (fromClient) s.OnClientPacket(ctx);
                        else s.OnServerPacket(ctx);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[{_service}] plugin threw on {arrow} 0x{opcode:X4}: {ex.Message}");
                    }
                }

                if (ctx.Forwarded is { } forward)
                {
                    if (fromClient) await SendToServerAsync(forward, ct);
                    else await SendToClientAsync(forward, ct);
                }
                foreach (var extra in ctx.ExtraToServer) await SendToServerAsync(extra, ct);
                foreach (var extra in ctx.ExtraToClient) await SendToClientAsync(extra, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The S→C seed packet (0x0807) carries the position the client's C→S stream starts from. Both
    /// cipher instances are built from it, and the packet itself still goes to the client unchanged.
    /// </summary>
    private void ArmCiphersIfSeed(byte[] body)
    {
        if (_decrypt is not null || _config.XorTable is null) return;
        if (!FiestaXorCipher.TryReadHandshakeSeed(body, out var seed)) return;
        _decrypt = new FiestaXorCipher(_config.XorTable, seed);
        _encrypt = new FiestaXorCipher(_config.XorTable, seed);
        Log.Debug($"[{_service}] bridge ciphers armed (seed=0x{seed:X4})");
    }

    public async Task SendToClientAsync(FiestaPacket packet, CancellationToken ct)
    {
        var wire = packet.ToBytes();                 // S→C is plaintext
        await _clientWrite.WaitAsync(ct);
        try { await _clientStream.WriteAsync(wire, ct); }
        finally { _clientWrite.Release(); }
    }

    public async Task SendToServerAsync(FiestaPacket packet, CancellationToken ct)
    {
        var wire = packet.ToBytes();
        var bodyOffset = wire[0] == 0x00 ? 3 : 1;
        await _serverWrite.WaitAsync(ct);
        try
        {
            // Encrypt in place, under the lock: the stream position must advance in exactly the
            // order the bytes hit the socket.
            _encrypt?.Transform(wire.AsSpan(bodyOffset));
            await _serverStream.WriteAsync(wire, ct);
        }
        finally { _serverWrite.Release(); }
    }

    private async Task WriteRawToServerAsync(byte[] wire, CancellationToken ct)
    {
        await _serverWrite.WaitAsync(ct);
        try { await _serverStream.WriteAsync(wire, ct); }
        finally { _serverWrite.Release(); }
    }
}
