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

    /// <summary>Space-separated hex of up to <paramref name="max"/> bytes,
    /// followed by "...+N" when truncated.</summary>
    public static string Hex(ReadOnlyMemory<byte> data, int max = 48)
    {
        if (data.Length == 0) return "(empty)";
        var n = Math.Min(data.Length, max);
        var sb = new System.Text.StringBuilder(n * 3 + 8);
        var span = data.Span;
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(span[i].ToString("X2"));
        }
        if (data.Length > n) sb.Append($" ...+{data.Length - n}");
        return sb.ToString();
    }
}
