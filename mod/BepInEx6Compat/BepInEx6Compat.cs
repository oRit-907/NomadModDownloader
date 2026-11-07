extern alias BE6;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;

namespace BepInEx6Compat
{
    /// <summary>
    /// Runs BepInEx 6 (Unity Mono) plugins on BepInEx 5. The mirror image of BepInEx 5 Compatibility.
    ///
    /// BepInEx 5 can't load BepInEx 6 plugins, and the two use different assembly names (BepInEx 5 is "BepInEx",
    /// BepInEx 6 is "BepInEx.Core" + "BepInEx.Unity.Mono"). So this plugin loads the real BepInEx 6 runtime from
    /// BepInEx\compat\BepInEx6 beside BepInEx 5 and starts BepInEx 6's own chainloader on BepInEx\plugins.
    /// BepInEx 6's chainloader only picks up plugins built for BepInEx 6, and BepInEx 5's only those built for 5,
    /// so each plugin is loaded exactly once. Harmony, MonoMod and Cecil are shared with BepInEx 5 (same names).
    /// BepInEx 6's log goes into BepInEx 5's LogOutput.log.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "akorit.bepinex6compat";
        public const string Name = "BepInEx 6 Compatibility";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static string CompatDir => Path.Combine(Path.Combine(Paths.BepInExRootPath, "compat"), "BepInEx6");

        private static bool _started;

        private void Awake()
        {
            Log = Logger;
            if (_started) return;
            _started = true;
            var core = Path.Combine(CompatDir, "BepInEx.Core.dll");
            if (!File.Exists(core))
            {
                Logger.LogError("BepInEx 6's runtime is missing (" + core + "), so BepInEx 6 mods can't load. Reinstall BepInEx with Nomad Mod Downloader.");
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            try
            {
                Host.Start();
            }
            catch (Exception e)
            {
                Logger.LogError("Couldn't start BepInEx 6 mods: " + (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e));
            }
        }

        /// <summary>
        /// Lets BepInEx 6 and its plugins find what they need: BepInEx 6's own files in compat\BepInEx6, and for
        /// Harmony/MonoMod/Cecil the copies BepInEx 5 already loaded.
        /// </summary>
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; } catch { return null; }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.ManifestModule is System.Reflection.Emit.ModuleBuilder) continue;
                    if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return a;
                }
                catch { }
            }
            foreach (var dir in new[] { CompatDir, Path.Combine(Paths.BepInExRootPath, "core") })
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

    /// <summary>Everything that touches BepInEx 6 types, kept out of Plugin so nothing loads before the resolver is in place.</summary>
    internal static class Host
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Start()
        {
            var root = Paths.BepInExRootPath;
            var configDir = Path.Combine(root, "config");
            var cfg6 = Path.Combine(configDir, "BepInEx6.cfg");

            // 1. BepInEx 6's paths: the same game, plugins and config folders as BepInEx 5, but its own settings
            //    file and cache, and its own files in compat\BepInEx6. (Before anything reads its config.)
            var search = new List<string> { Plugin.CompatDir, Path.Combine(root, "core") };
            if (Paths.DllSearchPaths != null) search.AddRange(Paths.DllSearchPaths);
            BE6::BepInEx.Paths.SetExecutablePath(Paths.ExecutablePath, root, Paths.ManagedPath, false, search.Distinct().ToArray());
            var p6 = typeof(BE6::BepInEx.Paths);
            Set(p6, "BepInExConfigPath", cfg6);
            Set(p6, "ConfigPath", Paths.ConfigPath);
            Set(p6, "PluginPath", Paths.PluginPath);
            Set(p6, "PatcherPluginPath", Paths.PatcherPluginPath);
            Set(p6, "CachePath", Path.Combine(Path.Combine(root, "cache"), "BepInEx6"));
            Set(p6, "BepInExAssemblyDirectory", Plugin.CompatDir);
            Set(p6, "BepInExAssemblyPath", Path.Combine(Plugin.CompatDir, "BepInEx.Core.dll"));

            // 2. BepInEx 5 already writes the log file, shows the console and listens to Unity: BepInEx 6 mustn't.
            WriteDefaults(cfg6);

            // 3. What the BepInEx 6 preloader would have worked out: the Unity version.
            try
            {
                typeof(BE6::BepInEx.Unity.Common.UnityInfo).GetMethod("Initialize", Any)
                    ?.Invoke(null, new object[] { Paths.ExecutablePath, Path.Combine(Paths.GameRootPath, Paths.ProcessName + "_Data") });
            }
            catch (Exception e) { Plugin.Log.LogDebug("UnityInfo: " + e.Message); }

            // 4. Start BepInEx 6's chainloader, with its log sent into BepInEx 5's log.
            var loader = new BE6::BepInEx.Unity.Mono.Bootstrap.UnityChainloader();
            typeof(BE6::BepInEx.Unity.Mono.Bootstrap.UnityChainloader).GetProperty("Instance", Any)?.GetSetMethod(true)?.Invoke(null, new object[] { loader });
            var listeners = BE6::BepInEx.Logging.Logger.Listeners;
            listeners.Add(new Forwarder()); // before Initialize, so its own errors reach the log too
            loader.Initialize(null);

            foreach (var l in listeners.Where(l => l.GetType().Name is "DiskLogListener" or "ConsoleLogListener" or "UnityLogListener").ToList())
            { listeners.Remove(l); try { l.Dispose(); } catch { } }
            var sources = BE6::BepInEx.Logging.Logger.Sources;
            // BepInEx 5 already logs Unity, Trace and Harmony messages; don't log them twice.
            foreach (var s in sources.Where(s => s.GetType().Name is "UnityLogSource" or "TraceLogSource" or "HarmonyLogSource").ToList())
            { sources.Remove(s); try { s.Dispose(); } catch { } }

            loader.Execute();

            var loaded = loader.Plugins.Values.Count(p => p.Instance != null);
            Plugin.Log.LogMessage(loaded == 1 ? "Started 1 BepInEx 6 mod." : "Started " + loaded + " BepInEx 6 mods.");
        }

        private static void Set(Type t, string prop, object value) =>
            t.GetProperty(prop, Any)?.GetSetMethod(true)?.Invoke(null, new[] { value });

        /// <summary>BepInEx 6's own settings, written once. The user can change the rest later.</summary>
        private static void WriteDefaults(string cfg)
        {
            var wanted = new Dictionary<string, Dictionary<string, string>>
            {
                ["Logging"] = new Dictionary<string, string> { ["UnityLogListening"] = "false", ["LogConsoleToUnityLog"] = "false" },
                ["Logging.Console"] = new Dictionary<string, string> { ["Enabled"] = "false" },
                ["Logging.Disk"] = new Dictionary<string, string> { ["Enabled"] = "false" },
            };
            var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).ToList() : new List<string> { "## BepInEx 6 settings, used by BepInEx 6 Compatibility on BepInEx 5. Logging stays off: BepInEx 5 writes the log." };
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

    /// <summary>Sends BepInEx 6 log messages to BepInEx 5, under the same source names.</summary>
    internal sealed class Forwarder : BE6::BepInEx.Logging.ILogListener
    {
        private readonly Dictionary<string, ManualLogSource> _sources = new Dictionary<string, ManualLogSource>();

        public BE6::BepInEx.Logging.LogLevel LogLevelFilter => BE6::BepInEx.Logging.LogLevel.All;

        public void LogEvent(object sender, BE6::BepInEx.Logging.LogEventArgs e)
        {
            try
            {
                var name = e.Source?.SourceName ?? "BepInEx 6";
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
