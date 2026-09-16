using System.Net.Sockets;

namespace FiestaProxy.Net;

/// <summary>One frame off the wire: the exact bytes read, and where the body sits inside them.</summary>
internal readonly record struct FiestaFrame(byte[] Wire, int BodyOffset, int BodyLength)
{
    /// <summary>A fresh copy of the body, safe to decrypt in place without disturbing Wire.</summary>
    public byte[] Body => Wire.AsSpan(BodyOffset, BodyLength).ToArray();
}

/// <summary>
/// Fiesta's length prefix, in one place so the passthrough pump and the bridge pump cannot drift.
///
///   * 1-byte inline length, 1..255:     wire = [len] + body
///   * 3-byte extended, 0x00 + LE u16:   wire = [00, lo, hi] + body
///
/// 0x00 is reserved as the extension marker, which is why an inline length can never be zero.
/// </summary>
internal static class FiestaFraming
{
    /// <summary>Read one complete frame, or null on EOF or malformed framing.</summary>
    public static async Task<FiestaFrame?> ReadAsync(NetworkStream s, CancellationToken ct)
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
        if (bodyLen < 2) return null; // malformed: the body must hold at least the 2-byte opcode
        if (!await ReadExactlyAsync(s, wire.AsMemory(prefixLen, bodyLen), ct)) return null;
        return new FiestaFrame(wire, prefixLen, bodyLen);
    }

    public static async Task<bool> ReadExactlyAsync(NetworkStream s, Memory<byte> buf, CancellationToken ct)
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
