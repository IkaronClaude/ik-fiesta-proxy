using FiestaProxy.Config;
using FiestaProxy.Net;

namespace FiestaProxy;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var config = ProxyConfig.FromEnvironment();
        Net.PacketLog.Enabled = config.PacketLogEnabled;
        Log.Info($"FiestaProxy starting.");
        Log.Info($"  packet log: {(config.PacketLogEnabled ? "ENABLED (PROXY_PACKET_LOG=1)" : "off — set PROXY_PACKET_LOG=1 to enable per-frame trace")}");
        Log.Info($"  upstream connect attempt timeout: {config.UpstreamConnectTimeout.TotalSeconds:N0}s (listeners health-gate on first reachability)");
        if (config.Routes.Count > 0)
        {
            Log.Info($"Client-facing routes:");
            foreach (var route in config.Routes)
                Log.Info($"  :{route.ListenPort,-5} -> {route.UpstreamHost}:{route.UpstreamPort}  ({route.ServiceName}, {route.Mode.ToString().ToLowerInvariant()})");
            Log.Info(config.XorTable is null
                ? "  XOR table not configured (BYO via XOR_TABLE_PATH / XOR_TABLE_HEX) — running NullCipher only"
                : $"  XOR table loaded: {config.XorTable.Length} bytes (BYO)");
        }
        if (config.S2sRoutes.Count > 0)
        {
            Log.Info($"S2S routes:");
            foreach (var r in config.S2sRoutes)
                Log.Info($"  {(r.IsInbound ? "inbound " : "outbound")} {r.BindAddress}:{r.ListenPort} -> {r.UpstreamHost}:{r.UpstreamPort}");
            Log.Info($"  inbound allow CIDRs: {string.Join(", ", config.S2sAllowedCidrs)}");
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        foreach (var r in config.Routes)
            tasks.Add(new ProxyListener(r, config).RunAsync(cts.Token));
        foreach (var r in config.S2sRoutes)
            tasks.Add(new S2sListener(r, config.S2sAllowedCidrs, config.UpstreamConnectTimeout).RunAsync(cts.Token));

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        return 0;
    }
}
