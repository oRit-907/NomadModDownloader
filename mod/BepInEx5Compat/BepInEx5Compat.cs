extern alias BE5;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;

namespace BepInEx5Compat
{
    /// <summary>
    /// Runs BepInEx 5 plugins on BepInEx 6 (Unity Mono).
    ///
    /// BepInEx 6 can't load BepInEx 5 plugins yet, and the two use different assembly names
    /// (BepInEx 5 is "BepInEx", BepInEx 6 is "BepInEx.Core" + "BepInEx.Unity.Mono"). So this plugin loads the real
    /// BepInEx 5 runtime from BepInEx\compat\BepInEx5 beside BepInEx 6 and starts BepInEx 5's own chainloader on
    /// BepInEx\plugins. BepInEx 5's chainloader only picks up plugins built for BepInEx 5, and BepInEx 6's only those
    /// built for 6, so each plugin is loaded exactly once. Harmony, MonoMod and Cecil are shared with BepInEx 6
    /// (same names, newer versions). BepInEx 5's log goes into BepInEx 6's LogOutput.log.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "akorit.bepinex5compat";
        public const string Name = "BepInEx 5 Compatibility";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static string CompatDir => Path.Combine(Path.Combine(Paths.BepInExRootPath, "compat"), "BepInEx5");

        private static bool _started;

        private void Awake()
        {
            Log = Logger;
            if (_started) return;
            _started = true;
            var be5 = Path.Combine(CompatDir, "BepInEx.dll");
            if (!File.Exists(be5))
            {
                Logger.LogError("BepInEx 5's runtime is missing (" + be5 + "), so BepInEx 5 mods can't load. Reinstall BepInEx with Nomad Mod Downloader.");
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            try
            {
                Host.Start();
            }
            catch (Exception e)
            {
                Logger.LogError("Couldn't start BepInEx 5 mods: " + (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e));
            }
        }

        /// <summary>
        /// Lets BepInEx 5 and its plugins find what they need: BepInEx 5's own files in compat\BepInEx5, and for
        /// Harmony/MonoMod/Cecil the copies BepInEx 6 already loaded (BepInEx 5 mods ask for older versions).
        /// </summary>
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; } catch { return null; }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.IsDynamic()) continue;
                    if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return a;
                }
                catch { }
            }
            var dirs = new List<string> { CompatDir, Path.Combine(Paths.BepInExRootPath, "core") };
            foreach (var dir in dirs)
            {
                var f = Path.Combine(dir, name + ".dll");
                if (File.Exists(f)) return Assembly.LoadFrom(f);
            }
            try
            {
                var plugin = Directory.GetFiles(Paths.PluginPath, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                if (plugin != null) return Assembly.LoadFrom(plugin);
            }
            catch { }
            return null;
        }
    }

    internal static class AssemblyExt
    {
        public static bool IsDynamic(this Assembly a)
        {
            try { return a.ManifestModule is System.Reflection.Emit.ModuleBuilder; } catch { return false; }
        }
    }

    /// <summary>Everything that touches BepInEx 5 types, kept out of Plugin so nothing loads before the resolver is in place.</summary>
    internal static class Host
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Start()
        {
            var root = Paths.BepInExRootPath;
            var core = Path.Combine(root, "core");
            var configDir = Path.Combine(root, "config");
            var cfg5 = Path.Combine(configDir, "BepInEx5.cfg");

            // 1. BepInEx 5's paths: the same game, plugins and config folders as BepInEx 6, but its own settings
            //    file and cache, and its own files in compat\BepInEx5.
            var paths5 = typeof(BE5::BepInEx.Paths);
            var search = new List<string> { Plugin.CompatDir, core };
            if (Paths.DllSearchPaths != null) search.AddRange(Paths.DllSearchPaths);
            paths5.GetMethod("SetExecutablePath", Any).Invoke(null, new object[] { Paths.ExecutablePath, root, Paths.ManagedPath, search.Distinct().ToArray() });
            Set(paths5, "BepInExConfigPath", cfg5);
            Set(paths5, "CachePath", Path.Combine(Path.Combine(root, "cache"), "BepInEx5"));
            Set(paths5, "BepInExAssemblyDirectory", Plugin.CompatDir);
            Set(paths5, "BepInExAssemblyPath", Path.Combine(Plugin.CompatDir, "BepInEx.dll"));

            // 2. BepInEx 6 already writes the log file and listens to Unity: BepInEx 5 mustn't do either.
            WriteDefaults(cfg5);

            // 3. Start BepInEx 5, with its log sent into BepInEx 6's log.
            BE5::BepInEx.Bootstrap.Chainloader.Initialize(null, false, null);
            var listeners = BE5::BepInEx.Logging.Logger.Listeners;
            foreach (var l in listeners.Where(l => l.GetType().Name == "UnityLogListener").ToList()) { listeners.Remove(l); l.Dispose(); }
            var sources = BE5::BepInEx.Logging.Logger.Sources;
            // BepInEx 6 already logs Trace and Harmony messages; don't log them twice.
            foreach (var s in sources.Where(s => s.GetType().Name is "TraceLogSource" or "HarmonyLogSource").ToList()) { sources.Remove(s); try { s.Dispose(); } catch { } }
            listeners.Add(new Forwarder());

            BE5::BepInEx.Bootstrap.Chainloader.Start();

            var loaded = BE5::BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Count(p => p.Instance != null);
            Plugin.Log.LogMessage(loaded == 1 ? "Started 1 BepInEx 5 mod." : "Started " + loaded + " BepInEx 5 mods.");
        }

        private static void Set(Type t, string prop, object value) =>
            t.GetProperty(prop, Any)?.GetSetMethod(true)?.Invoke(null, new[] { value });

        /// <summary>BepInEx 5's own settings, written once. The user can change the rest later.</summary>
        private static void WriteDefaults(string cfg)
        {
            var wanted = new Dictionary<string, Dictionary<string, string>>
            {
                ["Logging"] = new Dictionary<string, string> { ["UnityLogListening"] = "false", ["LogConsoleToUnityLog"] = "false" },
                ["Logging.Console"] = new Dictionary<string, string> { ["Enabled"] = "false" },
                ["Logging.Disk"] = new Dictionary<string, string> { ["Enabled"] = "false" },
            };
            var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).ToList() : new List<string> { "## BepInEx 5 settings, used by BepInEx 5 Compatibility on BepInEx 6. Logging stays off: BepInEx 6 writes the log." };
            foreach (var section in wanted)
            {
                var start = lines.FindIndex(l => l.Trim() == "[" + section.Key + "]");
                if (start < 0) { lines.Add(""); lines.Add("[" + section.Key + "]"); start = lines.Count - 1; }
                var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
                if (end < 0) end = lines.Count;
                foreach (var kv in section.Value)
                {
                    var i = lines.FindIndex(start + 1, end - start - 1, l => l.Split('=')[0].Trim() == kv.Key);
                    var line = kv.Key + " = " + kv.Value;
                    if (i >= 0) lines[i] = line; else { lines.Insert(end, line); end++; }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            File.WriteAllLines(cfg, lines.ToArray());
        }
    }

    /// <summary>Sends BepInEx 5 log messages to BepInEx 6, under the same source names (so the log reads the same as on BepInEx 5).</summary>
    internal sealed class Forwarder : BE5::BepInEx.Logging.ILogListener
    {
        private readonly Dictionary<string, ManualLogSource> _sources = new Dictionary<string, ManualLogSource>();

        public void LogEvent(object sender, BE5::BepInEx.Logging.LogEventArgs e)
        {
            try
            {
                var name = e.Source?.SourceName ?? "BepInEx 5";
                ManualLogSource s;
                lock (_sources)
                    if (!_sources.TryGetValue(name, out s)) _sources[name] = s = BepInEx.Logging.Logger.CreateLogSource(name);
                s.Log((LogLevel)(int)e.Level, e.Data);
            }
            catch { }
        }

        public void Dispose() { }
    }
}
