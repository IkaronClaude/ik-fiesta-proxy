using System.Reflection;
using System.Runtime.Loader;
using FiestaProxy.Config;

namespace FiestaProxy.Plugins;

/// <summary>
/// Loads plugin assemblies and hands out per-session plugin instances.
///
/// Plugins live in a directory (default `plugins/` beside the executable, or FIESTAPROXY_PLUGIN_DIR).
/// Every *.dll there is inspected for public non-abstract <see cref="IProxyPlugin"/> implementations
/// with a parameterless constructor. A plugin that throws while loading or initialising is logged and
/// skipped: a bad plugin must not stop the proxy from proxying.
///
/// Each assembly gets its own <see cref="AssemblyLoadContext"/> so plugins cannot fight over
/// dependency versions, with the host's own assemblies resolved from the default context so that
/// FiestaPacket and the plugin interfaces are the same types on both sides.
/// </summary>
public sealed class PluginHost
{
    private readonly List<IProxyPlugin> _plugins = new();

    public IReadOnlyList<IProxyPlugin> Plugins => _plugins;
    public bool Any => _plugins.Count > 0;

    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("FIESTAPROXY_PLUGIN_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "plugins");

    public static PluginHost Load(ProxyConfig config, string? directory = null)
    {
        var host = new PluginHost();
        var dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir))
        {
            Log.Debug($"plugins: no directory at {dir}, none loaded");
            return host;
        }

        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            // The host's own assemblies may sit beside the plugins when published; loading them as
            // plugins would duplicate the interface types and nothing would match.
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("FiestaProxy", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("FiestaLibReloaded", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var alc = new PluginLoadContext(dll);
                var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
                foreach (var type in asm.GetExportedTypes())
                {
                    if (!typeof(IProxyPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        Log.Warn($"plugins: {type.FullName} has no parameterless constructor, skipped");
                        continue;
                    }

                    var plugin = (IProxyPlugin)Activator.CreateInstance(type)!;
                    var ctx = new PluginHostContext(config, dir, SettingsFor(plugin.Name));
                    plugin.Initialise(ctx);
                    host._plugins.Add(plugin);
                    Log.Info($"plugins: loaded {plugin.Name} from {Path.GetFileName(dll)}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"plugins: {Path.GetFileName(dll)} failed to load: {ex.Message}");
            }
        }

        if (host._plugins.Count == 0)
            Log.Debug($"plugins: nothing loadable in {dir}");
        return host;
    }

    /// <summary>
    /// FIESTAPROXY_PLUGIN_&lt;NAME&gt;_&lt;KEY&gt;=value, with NAME upper-cased and non-alphanumerics
    /// replaced by underscores. So a plugin called "bridge-2026" reads BRIDGE_2026_SERVER from
    /// FIESTAPROXY_PLUGIN_BRIDGE_2026_SERVER.
    /// </summary>
    private static Dictionary<string, string> SettingsFor(string pluginName)
    {
        var slug = new string(pluginName.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        var prefix = $"FIESTAPROXY_PLUGIN_{slug}_";
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = (string)e.Key;
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                found[key[prefix.Length..]] = (string?)e.Value ?? string.Empty;
        }
        return found;
    }

    /// <summary>
    /// Offer a connection to every plugin. Returns the sessions that took it, or an empty list.
    /// A plugin that throws here is dropped for this session only.
    /// </summary>
    public List<IPluginSession> BeginSessions(PluginSessionInfo info)
    {
        var sessions = new List<IPluginSession>();
        foreach (var p in _plugins)
        {
            try
            {
                if (p.BeginSession(info) is { } s) sessions.Add(s);
            }
            catch (Exception ex)
            {
                Log.Warn($"plugins: {p.Name} BeginSession threw: {ex.Message}");
            }
        }
        return sessions;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
            => _resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Types shared with the host (FiestaPacket, IProxyPlugin) must resolve to the host's
            // copy or nothing will cast. Returning null defers to the default context, which is
            // exactly that; only genuinely private dependencies load from the plugin's own folder.
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name))
                return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
