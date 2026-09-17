namespace FiestaProxy.Net;

/// <summary>
/// Tiny helpers for human-readable packet logging. We dump a capped hex
/// preview of the payload so an operator can cross-reference opcodes with
/// PDB-derived layout tables (sizeof / field offsets in FiestaLib-Reloaded)
/// without flooding the proxy log with whole frames.
///
/// Per-packet logs are gated by <see cref="Enabled"/> (toggled from
/// <c>PROXY_PACKET_LOG=1</c> at startup, off by default). Structural events
/// — accept/close, route opens, health-gate transitions — go straight to
/// <see cref="Log"/> regardless.
/// </summary>
internal static class PacketLog
{
    /// <summary>Set once from ProxyConfig at startup.</summary>
    public static bool Enabled;

    /// <summary>Gated info log. No-op when packet logging is disabled.</summary>
    public static void Info(string message)
    {
        if (Enabled) Log.Info(message);
    }

    /// <summary>How many payload bytes a log line shows. 0 = all of them. Set once at startup from
    /// PROXY_PACKET_LOG_BYTES; the default stays small because a busy zone logs hundreds of frames a
    /// second, but a bridge being debugged wants every byte - a record list cut at 48 bytes cannot be
    /// decoded at all.</summary>
    public static int MaxBytes = 48;

    /// <summary>Space-separated hex of up to <paramref name="max"/> bytes (default: <see cref="MaxBytes"/>,
    /// 0 = unlimited). Truncation is announced loudly, because a quiet "...+N" has been read as the end of
    /// a payload more than once.</summary>
    public static string Hex(ReadOnlyMemory<byte> data, int max = -1)
    {
        if (max < 0) max = MaxBytes;
        if (max == 0) max = int.MaxValue;
        if (data.Length == 0) return "(empty)";
        var n = Math.Min(data.Length, max);
        var sb = new System.Text.StringBuilder(n * 3 + 8);
        var span = data.Span;
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(span[i].ToString("X2"));
        }
        if (data.Length > n) sb.Append($" !! ({data.Length - n} BYTES NOT DISPLAYED - set PROXY_PACKET_LOG_BYTES=0)");
        return sb.ToString();
    }
}
