using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace FiestaProxy.Service;

/// <summary>
/// The proxy as a registered Windows service. <c>FiestaProxy.exe --install</c> registers the exe with the SCM (auto
/// start, restart on failure) with <c>--service --config &lt;conf&gt;</c> on its command line; the SCM then starts it
/// with no console, no environment of ours and System32 as its working directory - which is why the settings come
/// from the conf file (Config/ConfFile.cs) and the log goes to a file beside the exe.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProxyService : ServiceBase
{
    private readonly Func<CancellationToken, Task<int>> _run;
    private CancellationTokenSource? _cts;
    private Task<int>? _task;

    public ProxyService(string name, Func<CancellationToken, Task<int>> run)
    {
        ServiceName = name;
        CanStop = true;
        CanShutdown = true;
        _run = run;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => _run(_cts.Token));
        // a proxy that cannot start (bad conf, port taken) must not sit there looking healthy: stop the service so the
        // SCM reports it and its recovery action (restart) applies
        _task.ContinueWith(t =>
        {
            if (_cts.IsCancellationRequested) return;
            Log.Error(t.IsFaulted ? $"proxy stopped: {t.Exception?.GetBaseException()}" : "proxy stopped by itself");
            ExitCode = 1;
            Stop();
        }, TaskScheduler.Default);
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        Log.Info("service stop requested");
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch { /* listeners unwinding */ }
    }

    public static int Install(string name, string? conf)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FiestaProxy.exe");
        var confPath = Config.ConfFile.Locate(conf);
        if (confPath is null)
        {
            Console.Error.WriteLine($"No {Config.ConfFile.DefaultName} beside the exe and no --config given - a service has no " +
                                    "environment, so it needs one. Nothing registered.");
            return 1;
        }
        // check it NOW, while someone is watching: a broken conf in a service only shows up in its log file
        try { Config.ConfFile.Parse(confPath); }
        catch (Exception e) { Console.Error.WriteLine($"{confPath}: {e.Message}\nNothing registered."); return 1; }

        var bin = $"\"{exe}\" --service --name \"{name}\" --config \"{confPath}\"";
        var rc = Sc("create", $"\"{name}\" binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"{name} (Fiesta proxy)\"");
        if (rc != 0) return rc;
        Sc("description", $"\"{name}\" \"Fiesta proxy - settings in {confPath}\"");
        Sc("failure", $"\"{name}\" reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        Console.WriteLine($"Registered '{name}'. Start it with:  sc start \"{name}\"   (log: {LogFilePath()})");
        return 0;
    }

    public static int Uninstall(string name)
    {
        Sc("stop", $"\"{name}\"");
        return Sc("delete", $"\"{name}\"");
    }

    public static string LogFilePath() => Path.Combine(AppContext.BaseDirectory, "FiestaProxy.log");

    private static int Sc(string verb, string args)
    {
        var psi = new ProcessStartInfo("sc.exe", $"{verb} {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.Error.WriteLine($"sc {verb}: {output}");
            if (p.ExitCode == 5) Console.Error.WriteLine("  -> run this from an ADMINISTRATOR prompt.");
        }
        return p.ExitCode;
    }
}
