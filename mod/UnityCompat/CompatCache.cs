using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace NomadUnityCompat
{
    /// <summary>
    /// Keeps BepInEx\unity-compat in step with the game: one restored copy of each stripped Unity module, built from the
    /// game's own module plus the full module of the same Unity version (BepInEx\unity-libs\&lt;version&gt;).
    /// Rebuilt only when the game, the Unity libraries or this patcher change.
    /// </summary>
    public static class CompatCache
    {
        public const string Version = "1.0.0";
        public const string ManifestName = "manifest.txt";

        public sealed class Result
        {
            public string Folder;
            public bool Ready;
            public int Rebuilt, Restored, Copied;
            public string Problem;
            public readonly List<string> Files = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        /// <param name="unityVersion">The game's Unity version, e.g. 6000.0.73f1.</param>
        public static Result Sync(string bepinexDir, string managedDir, string unityVersion, string[] searchDirs, Action<string> log)
        {
            var r = new Result { Folder = Path.Combine(bepinexDir, "unity-compat") };
            var libsVersion = LibsVersion(unityVersion);
            var libs = Path.Combine(Path.Combine(bepinexDir, "unity-libs"), libsVersion);
            var manifestPath = Path.Combine(r.Folder, ManifestName);
            var old = ReadManifest(manifestPath);

            if (!Directory.Exists(libs) || !Directory.GetFiles(libs, "UnityEngine*.dll").Any())
            {
                // Never leave restored files for another Unity version where the game would load them.
                Clear(r.Folder);
                r.Problem = $"the Unity {libsVersion} libraries aren't installed (BepInEx\\unity-libs\\{libsVersion})";
                return r;
            }

            Directory.CreateDirectory(r.Folder);
            var header = $"unity={unityVersion};compat={Version}";
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stale = !old.TryGetValue("", out var h) || h != header;
            if (stale) Clear(r.Folder);

            var gameFiles = Directory.GetFiles(managedDir, "UnityEngine*.dll")
                .ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);
            var fullFiles = Directory.GetFiles(libs, "UnityEngine*.dll")
                .ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);

            // Which modules need building.
            var todo = new List<string>();
            foreach (var name in fullFiles.Keys)
            {
                var key = Stamp(gameFiles.TryGetValue(name, out var g) ? g : null) + "|" + Stamp(fullFiles[name]);
                manifest[name] = key;
                var outFile = Path.Combine(r.Folder, name);
                if (!stale && old.TryGetValue(name, out var was) && was == key && File.Exists(outFile)) continue;
                todo.Add(name);
            }
            // Files no longer needed.
            foreach (var f in Directory.GetFiles(r.Folder, "*.dll"))
                if (!fullFiles.ContainsKey(Path.GetFileName(f))) TryDelete(f);

            if (todo.Count > 0)
            {
                log?.Invoke($"Restoring stripped Unity code in {todo.Count} module(s) (only after an install or a game update)...");
                var resolver = new Resolver();
                foreach (var d in searchDirs.Concat(new[] { managedDir })) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
                var fullResolver = new DefaultAssemblyResolver();
                fullResolver.AddSearchDirectory(libs);
                foreach (var d in searchDirs) if (Directory.Exists(d)) fullResolver.AddSearchDirectory(d);

                var merged = new List<(string name, AssemblyDefinition asm)>();
                var opened = new List<AssemblyDefinition>();
                try
                {
                    // Read every game module first, so writing one can see the others' restored types.
                    foreach (var name in todo)
                    {
                        if (!gameFiles.TryGetValue(name, out var gamePath)) continue;
                        var ga = AssemblyDefinition.ReadAssembly(gamePath, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
                        resolver.Register(ga);
                        opened.Add(ga);
                        merged.Add((name, ga));
                    }
                    foreach (var (name, ga) in merged)
                    {
                        using (var fa = AssemblyDefinition.ReadAssembly(fullFiles[name], new ReaderParameters { AssemblyResolver = fullResolver }))
                        {
                            var m = UnityMerger.Merge(ga, fa);
                            r.Restored += m.Total;
                            foreach (var w in m.Warnings) r.Warnings.Add(name + ": " + w);
                        }
                    }
                    foreach (var (name, ga) in merged)
                    {
                        var outFile = Path.Combine(r.Folder, name);
                        var tmp = outFile + ".tmp";
                        try
                        {
                            ga.Write(tmp);
                            TryDelete(outFile);
                            File.Move(tmp, outFile);
                            r.Rebuilt++;
                        }
                        catch (Exception e)
                        {
                            TryDelete(tmp);
                            TryDelete(outFile);
                            manifest.Remove(name);
                            r.Warnings.Add($"{name}: couldn't write the restored module ({e.Message}); the game's own copy is used.");
                        }
                    }
                }
                finally
                {
                    foreach (var a in opened) a.Dispose();
                }
                // Modules the game doesn't ship at all: the full ones, as they are.
                foreach (var name in todo.Where(n => !gameFiles.ContainsKey(n)))
                {
                    try { File.Copy(fullFiles[name], Path.Combine(r.Folder, name), true); r.Copied++; }
                    catch (Exception e) { manifest.Remove(name); r.Warnings.Add($"{name}: {e.Message}"); }
                }
                WriteManifest(manifestPath, header, manifest);
            }
            else if (stale) WriteManifest(manifestPath, header, manifest);

            r.Files.AddRange(Directory.GetFiles(r.Folder, "*.dll"));
            r.Ready = r.Files.Count > 0;
            return r;
        }

        /// <summary>"6000.0.73f1" → "6000.0.73" (how BepInEx names its Unity library downloads).</summary>
        public static string LibsVersion(string unityVersion)
        {
            var parts = (unityVersion ?? "").Split('.');
            if (parts.Length < 3) return unityVersion ?? "";
            var patch = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
            return parts[0] + "." + parts[1] + "." + patch;
        }

        private static string Stamp(string file)
        {
            if (file == null) return "-";
            var fi = new FileInfo(file);
            return fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
        }

        private static Dictionary<string, string> ReadManifest(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                var lines = File.ReadAllLines(path);
                if (lines.Length > 0) d[""] = lines[0];
                foreach (var l in lines.Skip(1))
                {
                    var i = l.IndexOf('=');
                    if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
            return d;
        }

        private static void WriteManifest(string path, string header, Dictionary<string, string> m)
        {
            var lines = new List<string> { header };
            lines.AddRange(m.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => k.Key + "=" + k.Value));
            File.WriteAllLines(path, lines.ToArray());
        }

        private static void Clear(string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder)) TryDelete(f);
        }

        private static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>Resolves to the in-memory (restored) modules, so writing one finds types restored in another.</summary>
        private sealed class Resolver : DefaultAssemblyResolver
        {
            public void Register(AssemblyDefinition a) => RegisterAssembly(a);
        }
    }
}
