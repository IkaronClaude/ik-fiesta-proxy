using System.Text;
using System.Text.RegularExpressions;

namespace FiestaProxy.Config;

/// <summary>
/// FiestaProxy.conf - the proxy's settings as a FILE, for when there is no environment to put them in (a registered
/// Windows service, a double-clicked exe). It is translated into the same environment variables the proxy and its
/// plugins already read, in-process and before anything else starts - so every consumer stays as it is, and a
/// variable that IS set in the environment always wins (docker / k8s keep working unchanged).
///
/// Found as <c>--config &lt;file&gt;</c>, else <c>FiestaProxy.conf</c> beside the exe. Lines are
/// <c>DIRECTIVE value</c>; <c>;</c> starts a comment; paths are relative to the conf file's folder.
/// <code>
///   #include "..\ServerSource\9Data\ServerInfo\ServerInfo.txt"   the server this proxy fronts
///   ADVERTISE_IP   192.168.1.10     the address players reach this machine on (-> PUBLIC_IP)
///   PORT_OFFSET    10000            player-facing port = the server's port + this (default 10000)
///   MODE           bridge           bridge | rewrite | opaque (default bridge)
///   LISTEN         Zone_0_3 29025   one service's player-facing port, overriding the offset
///   UPSTREAM_HOST  10.0.0.5         dial the server here instead of the IP ServerInfo lists
///   SET            NAME value       any environment variable; ${VAR} expands (see below)
///   PATH           NAME file        the same, the value resolved against the conf folder
/// </code>
/// The routes (PROXY_ROUTES) come from the included ServerInfo.txt: every client-facing SERVER_INFO row (kind 20 -
/// the ones the stock file marks PUBLIC_IP) becomes one route, named the way the server names its services: type 4 =
/// Login, 5 = WorldManager_&lt;world&gt;, 6 = Zone_&lt;world&gt;_&lt;zone&gt;. ${VAR} in SET / PATH values expands
/// the directives above (ADVERTISE_IP, PORT_OFFSET, MODE, UPSTREAM_HOST), LISTEN_&lt;service&gt; (each route's
/// player-facing port) and CONF_DIR.
/// </summary>
public static class ConfFile
{
    public const string DefaultName = "FiestaProxy.conf";

    /// <summary>The conf file to use: an explicit path, else FiestaProxy.conf beside the exe if there is one.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);
        var beside = Path.Combine(AppContext.BaseDirectory, DefaultName);
        return File.Exists(beside) ? beside : null;
    }

    /// <summary>Apply a conf file to this process's environment. Returns what it did, one line per variable.</summary>
    public static List<string> Apply(string path)
    {
        var vars = Parse(path);
        var done = new List<string>();
        foreach (var (name, value) in vars)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                done.Add($"{name}: kept the environment's value");
                continue;
            }
            Environment.SetEnvironmentVariable(name, value);
            done.Add($"{name}={value}");
        }
        return done;
    }

    /// <summary>The environment variables a conf file stands for, in order (no side effects - testable).</summary>
    public static List<(string Name, string Value)> Parse(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"config file not found: {path}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PORT_OFFSET"] = "10000",
            ["MODE"] = "bridge",
            ["CONF_DIR"] = dir,
        };
        var listen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<(string Name, string Value, bool IsPath)>();
        var services = new List<ServerService>();
        var includes = 0;

        var n = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            n++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var m = Regex.Match(line, @"^#include\s+""?([^""]+?)""?\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var inc = Resolve(dir, m.Groups[1].Value);
                services.AddRange(ReadServerInfo(inc));
                includes++;
                continue;
            }
            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0].ToUpperInvariant();
            string Arg(int i) => parts.Length > i ? parts[i] : throw new InvalidOperationException($"{path}:{n}: '{key}' needs {i} value(s)");
            switch (key)
            {
                case "ADVERTISE_IP" or "PORT_OFFSET" or "MODE" or "UPSTREAM_HOST":
                    d[key] = parts.Length > 2 ? parts[1] + " " + parts[2] : Arg(1);
                    break;
                case "LISTEN":
                    if (!int.TryParse(Arg(2), out var lp))
                        throw new InvalidOperationException($"{path}:{n}: LISTEN port is not a number: '{parts[2]}'");
                    listen[Arg(1)] = lp;
                    break;
                case "SET":
                    sets.Add((Arg(1), parts.Length > 2 ? parts[2] : "", false));
                    break;
                case "PATH":
                    sets.Add((Arg(1), Arg(2), true));
                    break;
                default:
                    throw new InvalidOperationException($"{path}:{n}: unknown directive '{parts[0]}'");
            }
        }

        var result = new List<(string, string)>();
        if (includes > 0)
        {
            if (services.Count == 0)
                throw new InvalidOperationException($"{path}: the included ServerInfo lists no client-facing SERVER_INFO row");
            if (!int.TryParse(d["PORT_OFFSET"], out var offset))
                throw new InvalidOperationException($"{path}: PORT_OFFSET is not a number: '{d["PORT_OFFSET"]}'");
            var routes = new List<string>();
            foreach (var s in services)
            {
                var lp = listen.TryGetValue(s.Name, out var o) ? o : s.Port + offset;
                d["LISTEN_" + s.Name] = lp.ToString();
                var host = d.TryGetValue("UPSTREAM_HOST", out var uh) ? uh : s.Host;
                routes.Add($"{lp}:{s.Name}:{host}:{s.Port}:{d["MODE"]}");
            }
            foreach (var name in listen.Keys)
                if (!services.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{path}: LISTEN names '{name}', which the included ServerInfo does not have");
            result.Add(("PROXY_ROUTES", string.Join(';', routes)));
        }
        if (d.TryGetValue("ADVERTISE_IP", out var ip))
            result.Add(("PUBLIC_IP", ip));
        foreach (var (name, value, isPath) in sets)
        {
            var v = Expand(value, d, path);
            result.Add((name, isPath ? Resolve(dir, v) : v));
        }
        return result;
    }

    public sealed record ServerService(string Name, string Host, int Port);

    /// <summary>The client-facing services of a ServerInfo.txt (EUC-KR; the fields read are ASCII).</summary>
    public static List<ServerService> ReadServerInfo(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"included ServerInfo not found: {path}");
        var list = new List<ServerService>();
        //   SERVER_INFO "PG_W00_Z00", 6, 0, 0, 20, "127.0.0.1", 9016, 100, 1500
        //               name          type world zone kind ip    port
        var rx = new Regex(@"^\s*SERVER_INFO\s+""[^""]*""\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*""([^""]*)""\s*,\s*(\d+)",
                           RegexOptions.IgnoreCase);
        foreach (var raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            var m = rx.Match(StripComment(raw));
            if (!m.Success || m.Groups[4].Value != "20") continue;
            int type = int.Parse(m.Groups[1].Value), world = int.Parse(m.Groups[2].Value), zone = int.Parse(m.Groups[3].Value);
            var name = type switch
            {
                4 => "Login",
                5 => $"WorldManager_{world}",
                6 => $"Zone_{world}_{zone}",
                _ => null,
            };
            if (name is null) continue;
            list.Add(new ServerService(name, m.Groups[5].Value, int.Parse(m.Groups[6].Value)));
        }
        return list;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ';' && !inQuote) return line[..i];
        }
        return line;
    }

    // a conf written on Windows (..\ServerSource\...) must also work on Linux
    private static string Resolve(string dir, string p)
    {
        if (Path.DirectorySeparatorChar == '/') p = p.Replace('\\', '/');
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(dir, p));
    }

    private static string Expand(string value, Dictionary<string, string> d, string path) =>
        Regex.Replace(value, @"\$\{([A-Za-z0-9_]+)\}", m =>
            d.TryGetValue(m.Groups[1].Value, out var v)
                ? v
                : throw new InvalidOperationException($"{path}: ${{{m.Groups[1].Value}}} is not defined"));
}
