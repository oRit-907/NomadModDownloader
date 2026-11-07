using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;

namespace NomadModManager
{
    internal enum ProblemKind { MissingDependency, DependencyNotLoaded, Incompatible, NewerCopy, WrongProcess, StrippedUnity, Error }

    /// <summary>Something BepInEx said about a mod this session, in plain words, with what to do about it.</summary>
    internal sealed class StartupProblem
    {
        public ProblemKind Kind;
        public string Plugin = "";   // the name BepInEx used
        public string Title = "";    // short: "Needs Long Haul API"
        public string Detail = "";   // the why
        public string Other = "";    // GUID/name of the other mod involved
        public bool Blocking;        // the mod didn't start because of it
    }

    /// <summary>
    /// Reads this session's BepInEx\LogOutput.log and explains, per mod, why it didn't start or what went wrong.
    /// The file is rewritten at every game start, so everything in it is about this session.
    /// </summary>
    internal static class StartupLog
    {
        private static readonly Dictionary<string, List<StartupProblem>> ByName = new Dictionary<string, List<StartupProblem>>();
        private static DateTime _time;
        private static long _size;

        public static string LogPath => Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
        public static bool UnityCompatInstalled => File.Exists(Path.Combine(Path.Combine(Paths.PatcherPluginPath, "NomadUnityCompat"), "NomadUnityCompat.dll"));

        // [Error  :   BepInEx] Could not load [Long Haul 1.2.0] because it has missing dependencies: akorit.longhaul.api (v1.0.0 or newer)
        private static readonly Regex Header = new Regex(@"^\[(?<lvl>\w+)\s*:\s*(?<src>[^\]]*?)\s*\]\s?(?<msg>.*)$");
        private static readonly Regex Bracketed = new Regex(@"\[(?<name>[^\[\]]+?)(?: (?<ver>v?\d[\w.\-+]*))?\]");

        public static void Refresh()
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (!fi.Exists) { ByName.Clear(); return; }
                if (fi.LastWriteTimeUtc == _time && fi.Length == _size) return;
                _time = fi.LastWriteTimeUtc; _size = fi.Length;
                string text;
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                Parse(text);
            }
            catch (Exception e) { ModManagerPlugin.Log?.LogInfo("Couldn't read the BepInEx log: " + e.Message); }
        }

        internal static void Parse(string text)
        {
            ByName.Clear();
            var lines = text.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var h = Header.Match(lines[i]);
                if (!h.Success) continue;
                var lvl = h.Groups["lvl"].Value;
                var src = h.Groups["src"].Value.Trim();
                var msg = h.Groups["msg"].Value.Trim();
                // The lines after a header that aren't headers themselves belong to it (stack traces).
                var more = new List<string>();
                for (var j = i + 1; j < lines.Length && j < i + 40 && !Header.IsMatch(lines[j]); j++) more.Add(lines[j]);
                var full = msg + "\n" + string.Join("\n", more.ToArray());

                if (src.StartsWith("BepInEx"))
                {
                    var b = Bracketed.Match(msg);
                    var name = b.Success ? b.Groups["name"].Value.Trim() : null;
                    if (name == null) continue;
                    if (msg.StartsWith("Could not load") && msg.Contains("missing dependencies"))
                    {
                        var deps = After(msg, "missing dependencies:");
                        Add(name, new StartupProblem
                        {
                            Kind = ProblemKind.MissingDependency, Blocking = true, Other = FirstGuid(deps),
                            Title = "Needs " + Title(Friendly(FirstGuid(deps))),
                            Detail = "It needs another mod that isn't installed or isn't switched on: " + deps + ".",
                        });
                    }
                    else if (msg.StartsWith("Could not load") && msg.Contains("incompatible with"))
                    {
                        var other = After(msg, "incompatible with:");
                        Add(name, new StartupProblem
                        {
                            Kind = ProblemKind.Incompatible, Blocking = true, Other = FirstGuid(other),
                            Title = "Conflicts with " + Title(Friendly(FirstGuid(other))),
                            Detail = "Its author says it doesn't work together with " + other + ". Switch one of the two off.",
                        });
                    }
                    else if (msg.StartsWith("Skipping") && msg.Contains("newer version exists"))
                    {
                        var other = After(msg, "newer version exists").Trim(' ', '(', ')');
                        Add(name, new StartupProblem
                        {
                            Kind = ProblemKind.NewerCopy, Blocking = true, Other = other,
                            Title = "Another copy is newer",
                            Detail = "This mod is installed twice, and BepInEx started the newer copy (" + other + ") instead of this one. You can remove this copy.",
                        });
                    }
                    else if (msg.StartsWith("Skipping") && msg.Contains("dependency that was not loaded"))
                    {
                        Add(name, new StartupProblem
                        {
                            Kind = ProblemKind.DependencyNotLoaded, Blocking = true,
                            Title = "A mod it needs didn't start",
                            Detail = "It needs another mod that didn't start this time. Fix that mod first (it's marked in this list).",
                        });
                    }
                    else if (msg.StartsWith("Skipping") && msg.Contains("process filters"))
                    {
                        Add(name, new StartupProblem
                        {
                            Kind = ProblemKind.WrongProcess, Blocking = true,
                            Title = "Made for a different game",
                            Detail = "This mod says it only runs in another game, so BepInEx skipped it.",
                        });
                    }
                    else if ((lvl == "Error" || lvl == "Fatal") && (msg.StartsWith("Error loading") || msg.Contains("failed to load") || msg.StartsWith("Could not load")))
                    {
                        Add(name, ErrorProblem(name, full, true));
                    }
                    continue;
                }

                // Errors a plugin logged itself, under its own name.
                if ((lvl == "Error" || lvl == "Fatal") && src.Length > 0)
                    Add(src, ErrorProblem(src, full, full.IndexOf("failed to start", StringComparison.OrdinalIgnoreCase) >= 0));
            }
        }

        private static StartupProblem ErrorProblem(string name, string full, bool blocking)
        {
            var unity = Regex.Match(full, @"(MissingMethodException|MissingFieldException|TypeLoadException)[^\n]*?(UnityEngine[\w.]*)");
            var exact = Regex.Match(full, @"(?:Method|Field) not found: (?:\S+ )?(UnityEngine\.[\w.]+)");
            if (exact.Success && unity.Success) unity = exact;
            if (unity.Success)
                return new StartupProblem
                {
                    Kind = ProblemKind.StrippedUnity, Blocking = blocking, Plugin = name,
                    Title = "Uses Unity code the game left out",
                    Detail = "Nomad Drive's Unity files only contain what the game itself uses, and this mod needs more (" + unity.Groups[unity.Groups.Count - 1].Value.Replace("UnityEngine.", "") + "). "
                             + (UnityCompatInstalled ? "Unity Compatibility puts it back: restart the game once so it can finish setting up."
                                                     : "Unity Compatibility puts it back. Set it up in Nomad Mod Downloader > Check BepInEx."),
                };
            var first = full.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return new StartupProblem
            {
                Kind = ProblemKind.Error, Blocking = blocking, Plugin = name,
                Title = blocking ? "Error while starting" : "Reported an error",
                Detail = Clip(first, 220),
            };
        }

        private static void Add(string name, StartupProblem p)
        {
            p.Plugin = name;
            var key = ModCatalog.NameKey(name);
            if (key.Length == 0) return;
            if (!ByName.TryGetValue(key, out var list)) ByName[key] = list = new List<StartupProblem>();
            // One of each kind is enough (a plugin can log the same error every frame).
            if (list.Any(x => x.Kind == p.Kind && x.Title == p.Title)) return;
            list.Add(p);
        }

        /// <summary>What BepInEx said about this mod (matched by the plugin names inside it and the mod's own name).</summary>
        public static List<StartupProblem> For(ModEntry m)
        {
            var keys = new HashSet<string> { ModCatalog.NameKey(m.Name) };
            foreach (var (_, n) in ModSettings.PluginsOf(m)) keys.Add(ModCatalog.NameKey(n));
            var result = new List<StartupProblem>();
            foreach (var k in keys)
                if (k.Length > 0 && ByName.TryGetValue(k, out var list)) result.AddRange(list);
            return result.OrderByDescending(p => p.Blocking).ThenBy(p => p.Kind).ToList();
        }

        public static bool AnyStrippedUnity => ByName.Values.Any(l => l.Any(p => p.Kind == ProblemKind.StrippedUnity));

        private static string After(string s, string marker)
        {
            var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? "" : s.Substring(i + marker.Length).Trim();
        }

        private static string FirstGuid(string list)
        {
            var s = list.Split(',')[0].Trim();
            var p = s.IndexOf(" (", StringComparison.Ordinal);
            return (p > 0 ? s.Substring(0, p) : s).Trim();
        }

        /// <summary>"akorit.longhaul.api" → "longhaul api"; plain names stay as they are.</summary>
        public static string Friendly(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid.Contains(" ")) return guid ?? "";
            var parts = guid.Split('.');
            var tail = parts.Length >= 2 ? string.Join(" ", parts.Skip(Math.Max(1, parts.Length - 2)).ToArray()) : guid;
            return tail.Replace('_', ' ').Replace('-', ' ');
        }

        private static string Title(string s) =>
            string.Join(" ", (s ?? "").Split(' ').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w).ToArray());

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
