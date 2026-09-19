using FiestaProxy.Config;
using FiestaProxy.Net;
using FiestaProxy.Plugins;

namespace FiestaProxy;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var config = ProxyConfig.FromEnvironment();
        Net.PacketLog.Enabled = config.PacketLogEnabled;
        if (int.TryParse(Environment.GetEnvironmentVariable("PROXY_PACKET_LOG_BYTES"), out var logBytes) && logBytes >= 0)
            Net.PacketLog.MaxBytes = logBytes;
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

        var plugins = PluginHost.Load(config);
        if (plugins.Any)
            Log.Info($"Plugins: {string.Join(", ", plugins.Plugins.Select(p => p.Name))}");
        else if (config.Routes.Any(r => r.Mode == RouteMode.Bridge))
            Log.Warn($"A bridge route is configured but no plugin loaded from {PluginHost.DefaultDirectory}");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        foreach (var r in config.Routes)
            tasks.Add(Watch(new ProxyListener(r, config, plugins).RunAsync(cts.Token), r.ServiceName, r.ListenPort));
        foreach (var r in config.S2sRoutes)
            tasks.Add(new S2sListener(r, config.S2sAllowedCidrs, config.UpstreamConnectTimeout).RunAsync(cts.Token));

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        return 0;
    }

    // Task.WhenAll reports a fault only after EVERY task has finished, and listeners run for ever - so a
    // listener that dies is otherwise never reported, and its port just stays closed. Say it the moment it
    // happens.
    private static Task Watch(Task listener, string service, int port) =>
        listener.ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Error($"[{service}] listener on :{port} DIED - its port is closed until restart: " +
                          t.Exception?.GetBaseException());
        }, TaskScheduler.Default);
}
