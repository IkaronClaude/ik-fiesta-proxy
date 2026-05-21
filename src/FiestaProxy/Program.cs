using FiestaProxy.Config;
using FiestaProxy.Net;

namespace FiestaProxy;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var config = ProxyConfig.FromEnvironment();
        Log.Info($"FiestaProxy starting. Listeners:");
        foreach (var route in config.Routes)
            Log.Info($"  :{route.ListenPort,-5} -> {route.UpstreamHost}:{route.UpstreamPort}  ({route.ServiceName})");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var listenerTasks = config.Routes
            .Select(r => new ProxyListener(r, config).RunAsync(cts.Token))
            .ToArray();

        try { await Task.WhenAll(listenerTasks); }
        catch (OperationCanceledException) { }
        return 0;
    }
}
