using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace NomadUnityCompat
{
    /// <summary>
    /// Unity Compatibility for BepInEx 5: the same restored Unity modules as the BepInEx 6 patcher, from a BepInEx 5
    /// preloader patcher (TargetDLLs / Initialize / Patch / Finish).
    /// </summary>
    public static class UnityCompatPatcher
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("Unity Compatibility");
        private static CompatCache.Result _cache;
        private static bool _synced;
        private static string CompatDir => Path.Combine(Paths.BepInExRootPath, "unity-compat");

        /// <summary>
        /// Normally doorstop already points Mono at BepInEx\unity-compat, so nothing needs patching. If it doesn't,
        /// the game's own Unity modules get the missing parts merged in as BepInEx loads them.
        /// </summary>
        public static IEnumerable<string> TargetDLLs
        {
            get
            {
                Sync();
                if (_cache == null || !_cache.Ready || SearchPathHasCompat()) return new string[0];
                return _cache.Files.Select(Path.GetFileName).Where(f => File.Exists(Path.Combine(Paths.ManagedPath, f))).ToArray();
            }
        }

        public static void Initialize() => Sync();

        public static void Patch(AssemblyDefinition assembly)
        {
            if (_cache == null || !_cache.Ready) return;
            var restored = Path.Combine(_cache.Folder, assembly.Name.Name + ".dll");
            if (!File.Exists(restored)) return;
            using (var full = AssemblyDefinition.ReadAssembly(restored, new ReaderParameters { AssemblyResolver = assembly.MainModule.AssemblyResolver }))
                UnityMerger.Merge(assembly, full);
        }

        /// <summary>Loads the restored modules now, before Unity asks for them, so it uses these and not the stripped ones.</summary>
        public static void Finish()
        {
            if (_cache == null || !_cache.Ready) return;
            var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetName().Name; } catch { return ""; } }), StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var f in _cache.Files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (loaded.Contains(name)) continue;
                if (!File.Exists(Path.Combine(Paths.ManagedPath, Path.GetFileName(f)))) continue; // the rest load on demand
                try { Assembly.LoadFrom(f); n++; }
                catch (Exception e) { Log.LogWarning($"Couldn't load restored {Path.GetFileName(f)}: {e.Message}"); }
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var f = Path.Combine(_cache.Folder, name + ".dll");
                return name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) && File.Exists(f) ? Assembly.LoadFrom(f) : null;
            };
            Log.LogInfo($"Unity Compatibility ready ({_cache.Files.Count} restored module(s), {n} loaded now).");
        }

        private static void Sync()
        {
            if (_synced) return;
            _synced = true;
            try
            {
                var version = UnityVersion();
                if (version == null) { Log.LogWarning("Unity Compatibility is off: couldn't tell which Unity version the game uses."); return; }
                var search = (Paths.DllSearchPaths ?? new string[0]).Where(d => !SamePath(d, CompatDir)).ToArray();
                _cache = CompatCache.Sync(Paths.BepInExRootPath, Paths.ManagedPath, version, search, m => Log.LogInfo(m));
                foreach (var w in _cache.Warnings.Take(20)) Log.LogWarning(w);
                if (_cache.Problem != null) Log.LogWarning("Unity Compatibility is off: " + _cache.Problem + ". Open Nomad Mod Downloader > Check BepInEx to fix it.");
                else if (_cache.Rebuilt + _cache.Copied > 0)
                    Log.LogMessage($"Restored {_cache.Restored} stripped Unity members in {_cache.Rebuilt} module(s), added {_cache.Copied} module(s) the game doesn't ship.");
            }
            catch (Exception e)
            {
                Log.LogError("Unity Compatibility couldn't prepare the restored Unity files: " + e);
                _cache = null;
            }
        }

        private static bool SearchPathHasCompat() => (Paths.DllSearchPaths ?? new string[0]).Any(d => SamePath(d, CompatDir));

        /// <summary>The game's Unity version (e.g. 6000.0.73f1), read from its data files the way BepInEx 6 does.</summary>
        private static string UnityVersion()
        {
            var data = Path.GetDirectoryName(Paths.ManagedPath);
            foreach (var (file, offsets) in new[] { ("globalgamemanagers", new[] { 20, 48 }), ("data.unity3d", new[] { 18 }), ("mainData", new[] { 20 }) })
            {
                var path = Path.Combine(data, file);
                if (!File.Exists(path)) continue;
                try
                {
                    using (var fs = File.OpenRead(path))
                        foreach (var off in offsets)
                        {
                            fs.Position = off;
                            var sb = new StringBuilder();
                            int b;
                            while ((b = fs.ReadByte()) > 0 && sb.Length < 32) sb.Append((char)b);
                            var s = sb.ToString();
                            if (Regex.IsMatch(s, @"^\d{4,}\.\d+\.\d+[abfpx]\d+$")) return s;
                        }
                }
                catch { }
            }
            return null;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
