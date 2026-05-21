using FiestaLibReloaded.Networking;

namespace FiestaProxy.Crypto;

/// <summary>
/// Fiesta Online's per-stream XOR cipher. Ported from
/// github.com/Ikaron/fiesta-filter (FilterLib/src/FilterStream.cpp).
///
/// Wire model:
///   * S→C is plaintext.
///   * C→S is encrypted once a handshake notification packet is observed S→C.
///     The handshake is a 4-byte plaintext packet on the S→C direction:
///         [0x07, 0x08, posLo, posHi]
///     where (posHi &lt;&lt; 8) | posLo seeds the starting position into the 499-byte
///     XOR table that the client uses for C→S traffic.
///   * The table is fixed across all servers/clients; only the start offset is
///     per-connection.
///   * Position is per-direction (one counter for receive, one for send) and
///     advances one byte per XOR'd byte, mod 515.
///
/// Each instance is single-direction. Use two instances per proxied connection
/// if you ever need to MITM C→S — one fed by the upstream-side stream and one
/// feeding the client-side stream.
///
/// This implementation is currently dormant: the proxy's only rewrites target
/// S→C packets (plaintext) and the C→S byte pump forwards opaque bytes without
/// inspecting them, so no cipher operation is performed on the hot path. The
/// class exists so future hooks that need to read C→S traffic can plug in.
/// </summary>
public sealed class FiestaXorCipher : IFiestaStreamCipher
{
    private static readonly byte[] XorTable =
    {
        0x00};

    private ushort _pos;

    public FiestaXorCipher(ushort startPos = 0)
    {
        _pos = (ushort)(startPos % XorTable.Length);
    }

    /// <summary>
    /// Try to parse the S→C handshake notification packet
    ///   [0x07, 0x08, posLo, posHi]
    /// and return the seed it carries. Length-prefix has already been stripped
    /// by FiestaConnection by the time you see the payload — pass the raw 4-byte
    /// frame body (opcode + 2 data bytes).
    /// </summary>
    public static bool TryReadHandshakeSeed(ReadOnlySpan<byte> frame, out ushort startPos)
    {
        if (frame.Length == 4 && frame[0] == 0x07 && frame[1] == 0x08)
        {
            startPos = (ushort)(frame[2] | (frame[3] << 8));
            return true;
        }
        startPos = 0;
        return false;
    }

    public void Transform(Span<byte> data)
    {
        var pos = _pos;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= XorTable[pos];
            pos = (ushort)((pos + 1) % XorTable.Length);
        }
        _pos = pos;
    }
}
