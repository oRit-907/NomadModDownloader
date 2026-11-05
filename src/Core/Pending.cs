
namespace NomadModDownloader.Core
{
    /// <summary>
    /// Changes that can't happen while Nomad Drive is running (the DLLs are in use), applied by the
    /// preloader patcher the next time the game starts, before any plugin loads.
    ///
    ///   BepInEx/NomadModManager/pending.txt   one "op|relative/path" per line:
    ///       remove|BepInEx/plugins/Some Mod      delete a mod's folder or DLL
    ///       disable|BepInEx/plugins/Some Mod     rename its *.dll to *.dll.disabled
    ///       enable|BepInEx/plugins/Some Mod      the reverse
    ///   *.nmm-new files under BepInEx/           a newer copy of an in-use file, swapped in at launch
    ///
    /// Shared with Nomad Mod Downloader, which writes the same format when it updates a mod while the game runs.
    /// </summary>
    /// <remarks>Same file format as the game-side Nomad Mod Manager (keep the two copies in step).</remarks>
    public static class Pending
    {
        public const string NewSuffix = ".nmm-new";
        public const string OffSuffix = ".disabled";

        public static string Dir(string gameRoot) => Path.Combine(Path.Combine(gameRoot, "BepInEx"), "NomadModManager");
        public static string ListFile(string gameRoot) => Path.Combine(Dir(gameRoot), "pending.txt");

        public static string Norm(string rel) => rel.Replace('\\', '/').Trim('/');

        /// <summary>Game root + relative path, refusing anything outside BepInEx.</summary>
        public static string Full(string gameRoot, string rel)
        {
            rel = Norm(rel);
            if (rel.Length == 0 || rel.Split('/').Any(s => s == ".." || s.Contains(":")))
                throw new InvalidOperationException("Unsafe path: " + rel);
            var full = Path.GetFullPath(Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            var bep = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(bep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path is outside BepInEx: " + rel);
            return full;
        }

        public static List<KeyValuePair<string, string>> Read(string gameRoot)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var f = ListFile(gameRoot);
                if (!File.Exists(f)) return list;
                foreach (var raw in File.ReadAllLines(f))
                {
                    var line = raw.Trim();
                    var bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    var op = line.Substring(0, bar).Trim().ToLowerInvariant();
                    var rel = Norm(line.Substring(bar + 1));
                    if (rel.Length == 0 || (op != "remove" && op != "disable" && op != "enable")) continue;
                    list.Add(new KeyValuePair<string, string>(op, rel));
                }
            }
            catch { }
            return list;
        }

        public static void Write(string gameRoot, List<KeyValuePair<string, string>> ops)
        {
            Directory.CreateDirectory(Dir(gameRoot));
            var f = ListFile(gameRoot);
            if (ops.Count == 0) { if (File.Exists(f)) File.Delete(f); return; }
            File.WriteAllLines(f, ops.Select(o => o.Key + "|" + o.Value).ToArray());
        }

        /// <summary>Queues an op. disable/enable cancel each other; remove replaces both.</summary>
        public static void Add(string gameRoot, string op, string rel)
        {
            rel = Norm(rel);
            var ops = Read(gameRoot);
            bool Same(KeyValuePair<string, string> o) => o.Value.Equals(rel, StringComparison.OrdinalIgnoreCase);
            if (op == "disable" && ops.RemoveAll(o => Same(o) && o.Key == "enable") > 0) { Write(gameRoot, ops); return; }
            if (op == "enable" && ops.RemoveAll(o => Same(o) && o.Key == "disable") > 0) { Write(gameRoot, ops); return; }
            if (op == "remove") ops.RemoveAll(Same);
            if (!ops.Any(o => Same(o) && o.Key == op)) ops.Add(new KeyValuePair<string, string>(op, rel));
            Write(gameRoot, ops);
        }

        public static void Cancel(string gameRoot, string op, string rel)
        {
            rel = Norm(rel);
            var ops = Read(gameRoot);
            ops.RemoveAll(o => o.Key == op && o.Value.Equals(rel, StringComparison.OrdinalIgnoreCase));
            Write(gameRoot, ops);
        }

        /// <summary>All *.nmm-new files waiting under BepInEx (plugins, patchers, config).</summary>
        public static List<string> StagedFiles(string gameRoot)
        {
            var result = new List<string>();
            foreach (var sub in new[] { "plugins", "patchers", "config" })
            {
                var d = Path.Combine(Path.Combine(gameRoot, "BepInEx"), sub);
                if (!Directory.Exists(d)) continue;
                try { result.AddRange(Directory.GetFiles(d, "*" + NewSuffix, SearchOption.AllDirectories)); } catch { }
            }
            return result;
        }

        public static bool HasWork(string gameRoot) => Read(gameRoot).Count > 0 || StagedFiles(gameRoot).Count > 0;

        /// <summary>Applies everything. Ops that fail stay queued for next time. Returns how many changes were made.</summary>
        public static int ApplyAll(string gameRoot, Action<string> log)
        {
            var done = 0;
            var ops = Read(gameRoot);
            var failed = new List<KeyValuePair<string, string>>();

            // 1. removals
            foreach (var o in ops.Where(o => o.Key == "remove"))
            {
                try
                {
                    var full = Full(gameRoot, o.Value);
                    if (Directory.Exists(full)) Directory.Delete(full, true);
                    foreach (var f in new[] { full, full + OffSuffix })
                        if (File.Exists(f)) File.Delete(f);
                    log("Removed " + o.Value);
                    done++;
                }
                catch (InvalidOperationException e) { log("Dropped unsafe entry: " + e.Message); }
                catch (Exception e) { log("Couldn't remove " + o.Value + ": " + e.Message); failed.Add(o); }
            }

            // 2. newer copies of files that were in use
            foreach (var staged in StagedFiles(gameRoot))
            {
                try
                {
                    var target = staged.Substring(0, staged.Length - NewSuffix.Length);
                    // A disabled mod stays disabled after its update.
                    if (!File.Exists(target) && File.Exists(target + OffSuffix)) target += OffSuffix;
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(staged, target);
                    log("Updated " + Path.GetFileName(target));
                    done++;
                }
                catch (Exception e) { log("Couldn't update " + Path.GetFileName(staged) + ": " + e.Message); }
            }

            // 3. enable / disable
            foreach (var o in ops.Where(o => o.Key == "disable" || o.Key == "enable"))
            {
                try
                {
                    SetEnabled(Full(gameRoot, o.Value), o.Key == "enable");
                    log((o.Key == "enable" ? "Enabled " : "Disabled ") + o.Value);
                    done++;
                }
                catch (InvalidOperationException e) { log("Dropped unsafe entry: " + e.Message); }
                catch (Exception e) { log("Couldn't " + o.Key + " " + o.Value + ": " + e.Message); failed.Add(o); }
            }

            try { Write(gameRoot, failed); } catch { }
            return done;
        }

        /// <summary>Renames the DLLs of a mod folder (or a single DLL) between *.dll and *.dll.disabled.</summary>
        public static void SetEnabled(string full, bool enabled)
        {
            IEnumerable<string> files;
            if (Directory.Exists(full)) files = Directory.GetFiles(full, "*", SearchOption.AllDirectories);
            else files = new[] { full, full + OffSuffix };
            foreach (var f in files.ToList())
            {
                if (!enabled && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                {
                    if (File.Exists(f + OffSuffix)) File.Delete(f + OffSuffix);
                    File.Move(f, f + OffSuffix);
                }
                else if (enabled && f.EndsWith(".dll" + OffSuffix, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                {
                    var on = f.Substring(0, f.Length - OffSuffix.Length);
                    if (File.Exists(on)) File.Delete(on);
                    File.Move(f, on);
                }
            }
        }
    }
}
