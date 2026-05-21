using FiestaLibReloaded.Networking;

namespace FiestaProxy.Crypto;

/// <summary>
/// Fiesta Online's per-stream XOR cipher. Same shape as
/// github.com/Ikaron/fiesta-filter (FilterLib/src/FilterStream.cpp).
///
/// Wire model:
///   * S→C is plaintext.
///   * C→S is encrypted once a handshake notification packet is observed S→C.
///     The handshake is a 4-byte plaintext packet on the S→C direction:
///         [0x07, 0x08, posLo, posHi]
///     where (posHi &lt;&lt; 8) | posLo seeds the starting position into the XOR
///     table that the client uses for C→S traffic.
///   * Position is per-direction (one counter for receive, one for send) and
///     advances one byte per XOR'd byte, mod tableLen.
///
/// The cipher table itself is **bring-your-own** (BYO) — operators supply it
/// via the XOR_TABLE_PATH / XOR_TABLE_HEX env var. The proxy ships no table
/// (different server builds may use different tables; the table is also part
/// of the protocol-licensing question we don't want to take a stance on).
///
/// Currently dormant: the proxy's only rewrites target S→C packets (plaintext)
/// and the C→S byte pump forwards opaque bytes without inspecting them, so no
/// cipher operation is performed on the hot path. Plumbed so future hooks
/// that need to read or modify C→S traffic can construct one of these with
/// the operator-supplied table and the seed parsed from the S→C handshake.
/// </summary>
public sealed class FiestaXorCipher : IFiestaStreamCipher
{
    private readonly byte[] _table;
    private int _pos;

    public FiestaXorCipher(byte[] table, ushort startPos = 0)
    {
        if (table is null || table.Length == 0)
            throw new ArgumentException("XOR table must be non-empty", nameof(table));
        _table = table;
        _pos = startPos % table.Length;
    }

    /// <summary>
    /// Try to parse the S→C handshake notification packet
    ///   [0x07, 0x08, posLo, posHi]
    /// and return the seed it carries. Pass the raw 4-byte frame body
    /// (opcode + 2 data bytes) — length prefix already stripped.
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
        var tbl = _table;
        var n = tbl.Length;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= tbl[pos];
            pos++;
            if (pos >= n) pos -= n;
        }
        _pos = pos;
    }
}
