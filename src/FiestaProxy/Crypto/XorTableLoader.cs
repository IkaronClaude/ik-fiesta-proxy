using System.Globalization;

namespace FiestaProxy.Crypto;

/// <summary>
/// Loads the BYO XOR table from the environment.
///
/// Sources, in priority order:
///   1. XOR_TABLE_HEX        — inline hex string (e.g. "07594A...8DEB"),
///                             whitespace + 0x prefixes + commas tolerated.
///   2. XOR_TABLE_PATH       — path to a file containing either a hex string
///                             (any of the above formats) or the raw binary
///                             table. We try hex first; if the file content
///                             can't be parsed as hex we treat it as binary.
///   3. (neither set)        — returns null. The proxy still runs with
///                             NullCipher on both directions; only future
///                             features that need to decrypt C→S will throw
///                             when they try to access the table.
/// </summary>
public static class XorTableLoader
{
    public static byte[]? FromEnvironment()
    {
        var hex = Environment.GetEnvironmentVariable("XOR_TABLE_HEX");
        if (!string.IsNullOrWhiteSpace(hex))
            return ParseHex(hex)
                ?? throw new InvalidOperationException("XOR_TABLE_HEX is set but not valid hex");

        var path = Environment.GetEnvironmentVariable("XOR_TABLE_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"XOR_TABLE_PATH '{path}' does not exist");
            var bytes = File.ReadAllBytes(path);
            // Try hex-as-text first; if every byte is a hex-allowed char we
            // parse it as a hex string. Otherwise treat as raw binary.
            if (LooksLikeHexText(bytes))
            {
                var asText = System.Text.Encoding.ASCII.GetString(bytes);
                var parsed = ParseHex(asText);
                if (parsed is not null) return parsed;
            }
            return bytes;
        }

        return null;
    }

    private static bool LooksLikeHexText(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        var seenHex = false;
        foreach (var b in bytes)
        {
            // allow whitespace, comma, x, X (for 0x), and hex digits
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)'x' or (byte)'X')
                continue;
            var ok =
                (b >= (byte)'0' && b <= (byte)'9') ||
                (b >= (byte)'a' && b <= (byte)'f') ||
                (b >= (byte)'A' && b <= (byte)'F');
            if (!ok) return false;
            seenHex = true;
        }
        return seenHex;
    }

    private static byte[]? ParseHex(string s)
    {
        // strip whitespace, commas, "0x" prefixes
        var cleaned = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') continue;
            if ((c == '0') && i + 1 < s.Length && (s[i + 1] is 'x' or 'X'))
            { i++; continue; }
            cleaned.Append(c);
        }
        if (cleaned.Length == 0 || (cleaned.Length & 1) != 0) return null;
        var result = new byte[cleaned.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.ToString(i * 2, 2),
                               NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                               out var b))
                return null;
            result[i] = b;
        }
        return result;
    }
}
