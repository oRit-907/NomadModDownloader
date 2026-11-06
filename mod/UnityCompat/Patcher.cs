using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Unity.Common;
using Mono.Cecil;

namespace NomadUnityCompat
{
    /// <summary>
    /// Unity Compatibility: Nomad Drive ships Unity with its code stripped, so mods that call Unity methods the game
    /// itself never used (AssetBundle.LoadFromFile, XR input, ...) fail with MissingMethodException. This patcher gives
    /// the game restored Unity modules — its own, plus what was stripped — before Unity loads them.
    /// </summary>
    [PatcherPluginInfo("akorit.nomad.unitycompat", "Unity Compatibility", CompatCache.Version)]
    public sealed class UnityCompatPatcher : BasePatcher
    {
        private CompatCache.Result _cache;

        public UnityCompatPatcher()
        {
            // Runs before BepInEx reads the game's assemblies, so the restored files are in place for it and for Unity.
            try
            {
                var version = UnityInfo.Version.ToString();
                _cache = CompatCache.Sync(Paths.BepInExRootPath, Paths.ManagedPath, version,
                    Paths.DllSearchPaths.Where(d => !SamePath(d, Path.Combine(Paths.BepInExRootPath, "unity-compat"))).ToArray(),
                    m => Log.LogInfo(m));
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

        /// <summary>
        /// If BepInEx read a module from the game's folder instead of BepInEx\unity-compat (the doorstop search path isn't
        /// set), swap the restored module in here.
        /// </summary>
        [TargetAssembly("_all")]
        public bool Patch(AssemblyDefinition assembly, string fileName)
        {
            if (_cache == null || !_cache.Ready || !fileName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Context.AvailableAssembliesPaths.TryGetValue(fileName, out var from) || IsInCompat(from)) return false;
            var restored = Path.Combine(_cache.Folder, fileName);
            if (!File.Exists(restored)) return false;
            // Pull in what the restored module has and the game's doesn't (same merge, from our already-merged file).
            using (var full = AssemblyDefinition.ReadAssembly(restored, new ReaderParameters { AssemblyResolver = assembly.MainModule.AssemblyResolver }))
            {
                var m = UnityMerger.Merge(assembly, full);
                return m.Total > 0;
            }
        }

        /// <summary>Loads the restored modules now, before Unity asks for them, so it uses these and not the stripped ones.</summary>
        public override void Finalizer()
        {
            if (_cache == null || !_cache.Ready) return;
            var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name), StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var f in _cache.Files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (loaded.Contains(name)) continue;
                // Modules the game doesn't ship are loaded on demand by name only.
                if (!File.Exists(Path.Combine(Paths.ManagedPath, Path.GetFileName(f)))) continue;
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

        private bool IsInCompat(string path) =>
            _cache != null && SamePath(Path.GetDirectoryName(path), _cache.Folder);

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
