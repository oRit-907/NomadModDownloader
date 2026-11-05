using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using Newtonsoft.Json.Linq;

namespace NomadModManager
{
    internal enum ModState
    {
        Running,          // loaded at startup
        StartedNow,       // hot-loaded from the Mods menu, no restart
        Disabled,
        Settling,         // just appeared, waiting for the files to finish copying
        NeedsRestart,     // installed, but can't start until the game restarts
        WillDisable,      // queued for next launch
        WillEnable,
        WillRemove,
        Library,          // support DLLs only
        NotLoaded,        // has DLLs that BepInEx didn't load (see the log)
        Failed            // hot-load threw
    }

    internal sealed class ModEntry
    {
        public string Rel = "";            // BepInEx/plugins/<entry>
        public string Full = "";
        public bool IsFolder;
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Summary = "";
        public string Category = Categories.Misc;
        public ModState State;
        public string Note = "";
        public bool UpdateStaged;          // a newer copy is waiting for the next launch
        public bool InUse;                 // one of its DLLs is loaded in the game
        public bool IsSelf;
        public int NexusId;
        public string NexusVersion = "";
        public bool UpdateAvailable;
        public readonly List<string> Dlls = new List<string>();
        public readonly List<string> OffDlls = new List<string>();
        public readonly List<string> Guids = new List<string>();

        public bool CanRestartFix => State == ModState.NeedsRestart || State == ModState.WillDisable || State == ModState.WillEnable || State == ModState.WillRemove || State == ModState.Failed || UpdateStaged;
    }

    /// <summary>Everything in BepInEx\plugins, what state it's in, and the hot-load bookkeeping.</summary>
    internal sealed class ModCatalog
    {
        public List<ModEntry> Mods { get; private set; } = new List<ModEntry>();
        public event Action<string, bool> Toast; // message, good

        private readonly HashSet<string> _startupDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HotVerdict> _verdicts = new Dictionary<string, HotVerdict>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hotLoaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dll -> error ("" = ok)
        private readonly Dictionary<string, (long Size, DateTime Time, int Seen)> _settle = new Dictionary<string, (long, DateTime, int)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DateTime Time, string Name, string Version)> _meta = new Dictionary<string, (DateTime, string, string)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (string Version, string Author, string Name, string Summary)> _nexus = new Dictionary<int, (string, string, string, string)>();
        private JArray _downloaderManifest;
        private DateTime _manifestTime;

        public bool AutoStart = true;
        public string GameRoot => Paths.GameRootPath;

        public void SnapshotStartup()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                    _startupDlls.Add(Path.GetFullPath(f));
            }
            catch { }
        }

        public IEnumerable<int> NexusIds => Mods.Where(m => m.NexusId > 0).Select(m => m.NexusId).Distinct();
        public bool IsInstalled(int nexusId) => Mods.Any(m => m.NexusId == nexusId && m.State != ModState.WillRemove);

        public const string UnknownAuthor = "Unknown author";

        /// <summary>Nexus mods seen while browsing, by simplified name, so mods added by hand can be matched to their page.</summary>
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>();

        public static string NameKey(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in name ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        public void LearnNames(IEnumerable<NexusMod> mods)
        {
            foreach (var m in mods)
            {
                var k = NameKey(m.Name);
                if (k.Length >= 4 && !_byName.ContainsKey(k)) _byName[k] = m.Id;
            }
        }

        public void SetNexusInfo(int id, string version, string author, string name, string summary = null)
        {
            var keep = summary ?? (_nexus.TryGetValue(id, out var old) ? old.Summary : "");
            _nexus[id] = (version ?? "", author ?? "", name ?? "", keep ?? "");
        }

        public int RestartItems => Mods.Count(m => m.CanRestartFix && !IsHotReady(m));
        public static bool IsHotReady(ModEntry m) => m.State == ModState.NeedsRestart && (m.Note.StartsWith("Starting") || m.Note == "Ready to start");
        public int UpdateCount => Mods.Count(m => m.UpdateAvailable && !m.UpdateStaged);

        // ------------------------------------------------------------------
        // Scan

        public void Refresh()
        {
            ReadDownloaderManifest();
            var pending = Pending.Read(GameRoot);
            var staged = Pending.StagedFiles(GameRoot).Select(Path.GetFullPath).ToList();
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) loadedPaths.Add(Path.GetFullPath(a.Location)); } catch { }
            }

            var list = new List<ModEntry>();
            if (!Directory.Exists(Paths.PluginPath)) { Mods = list; return; }

            foreach (var entry in Directory.GetFileSystemEntries(Paths.PluginPath))
            {
                var full = Path.GetFullPath(entry);
                var isDir = Directory.Exists(full);
                var name = Path.GetFileName(full);
                if (!isDir)
                {
                    if (name.EndsWith(".dll" + Pending.OffSuffix, StringComparison.OrdinalIgnoreCase)) full = full.Substring(0, full.Length - Pending.OffSuffix.Length);
                    else if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Any(m => m.Full.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                }

                var m = new ModEntry { Full = full, IsFolder = isDir, Rel = "BepInEx/plugins/" + Path.GetFileName(full) };
                var files = isDir ? SafeFiles(full) : new List<string> { full, full + Pending.OffSuffix }.Where(File.Exists).ToList();
                m.Dlls.AddRange(files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
                m.OffDlls.AddRange(files.Where(f => f.EndsWith(".dll" + Pending.OffSuffix, StringComparison.OrdinalIgnoreCase)));
                if (m.Dlls.Count == 0 && m.OffDlls.Count == 0) continue;
                m.UpdateStaged = staged.Any(s => isDir ? s.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                       : s.StartsWith(full, StringComparison.OrdinalIgnoreCase));
                m.InUse = m.Dlls.Concat(m.OffDlls.Select(d => d.Substring(0, d.Length - Pending.OffSuffix.Length))).Any(d => loadedPaths.Contains(d));

                // Plugins running from this entry
                foreach (var info in Chainloader.PluginInfos.Values)
                {
                    if (info?.Location == null || info.Instance == null) continue;
                    var loc = Path.GetFullPath(info.Location);
                    if (loc.Equals(full, StringComparison.OrdinalIgnoreCase) || loc.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        m.Guids.Add(info.Metadata.GUID);
                        if (m.Name.Length == 0) { m.Name = info.Metadata.Name; m.Version = info.Metadata.Version.ToString(); }
                    }
                }
                if (m.Name.Length == 0) ReadMeta(m);
                if (m.Name.Length == 0) m.Name = Path.GetFileNameWithoutExtension(full);
                m.IsSelf = m.Guids.Contains(ModManagerPlugin.Guid);

                MatchDownloader(m);
                if (m.NexusId == 0 && _byName.TryGetValue(NameKey(m.Name), out var byName)) m.NexusId = byName;
                if (m.NexusId > 0 && _nexus.TryGetValue(m.NexusId, out var nx))
                {
                    m.NexusVersion = nx.Version;
                    if (nx.Author.Length > 0) m.Author = nx.Author;
                    m.Summary = nx.Summary;
                    m.UpdateAvailable = IsNewer(nx.Version, m.Version);
                }
                m.Category = m.IsSelf ? "Tools & Frameworks"
                    : m.NexusId > 0 && _nexus.TryGetValue(m.NexusId, out var nn) && nn.Name.Length > 0 ? Categories.Classify(nn.Name, nn.Summary)
                    : Categories.Classify(m.Name, string.Join(" ", m.Guids.ToArray()));

                // State
                var op = pending.FirstOrDefault(o => o.Value.Equals(m.Rel, StringComparison.OrdinalIgnoreCase)).Key;
                if (op == "remove") m.State = ModState.WillRemove;
                else if (op == "disable") m.State = ModState.WillDisable;
                else if (op == "enable") m.State = ModState.WillEnable;
                else if (m.Guids.Count > 0)
                {
                    var hot = m.Dlls.Any(d => _hotLoaded.TryGetValue(d, out var err) && err.Length == 0);
                    m.State = hot ? ModState.StartedNow : ModState.Running;
                    if (m.Dlls.Count == 0 && m.OffDlls.Count > 0) m.State = ModState.WillDisable; // renamed by hand while running
                }
                else if (m.Dlls.Count == 0) m.State = ModState.Disabled;
                else ClassifyNotRunning(m);

                list.Add(m);
            }

            Mods = list.OrderBy(x => Categories.Rank(x.Category)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A mod with DLLs that BepInEx isn't running: brand new (hot-load candidate), a library, or something that failed at startup.</summary>
        private void ClassifyNotRunning(ModEntry m)
        {
            var fresh = m.Dlls.Where(d => !_startupDlls.Contains(d)).ToList();
            if (fresh.Count == 0)
            {
                var anyPlugin = m.Dlls.Select(GetVerdict).Any(v => v != null && !v.NotAPlugin);
                m.State = anyPlugin ? ModState.NotLoaded : ModState.Library;
                m.Note = anyPlugin ? "BepInEx didn't start it — check BepInEx\\LogOutput.log" : "Support files only";
                return;
            }

            foreach (var d in fresh)
            {
                if (_hotLoaded.TryGetValue(d, out var err) && err.Length > 0) { m.State = ModState.Failed; m.Note = err; return; }
            }
            if (fresh.Any(d => !Settled(d))) { m.State = ModState.Settling; m.Note = "Finishing install…"; return; }

            var verdicts = fresh.Select(GetVerdict).Where(v => v != null).ToList();
            var plugins = verdicts.Where(v => !v.NotAPlugin).ToList();
            if (plugins.Count == 0) { m.State = ModState.Library; m.Note = "Support files only"; return; }
            if (plugins.All(v => v.WrongGame)) { m.State = ModState.NotLoaded; m.Note = "Made for a different game"; return; }
            var blocked = plugins.FirstOrDefault(v => !v.CanLoad && !v.WrongGame);
            if (blocked != null) { m.State = ModState.NeedsRestart; m.Note = blocked.Reason; return; }
            m.State = ModState.NeedsRestart;
            m.Note = AutoStart ? "Starting…" : "Ready to start";
        }

        /// <summary>Starts new mods that can be loaded without a restart. Called from the poll loop.</summary>
        public void StartNewMods(bool force = false)
        {
            if (!AutoStart && !force) return;
            foreach (var m in Mods.Where(x => x.State == ModState.NeedsRestart && (x.Note.StartsWith("Starting") || x.Note == "Ready to start")).ToList())
                TryStart(m);
        }

        public void TryStart(ModEntry m)
        {
            var fresh = m.Dlls.Where(d => !_startupDlls.Contains(d) && !_hotLoaded.ContainsKey(d)).ToList();
            var verdicts = fresh.Select(GetVerdict).Where(v => v != null && v.CanLoad).ToList();
            var started = new List<string>();
            foreach (var v in verdicts)
            {
                try
                {
                    started.AddRange(HotLoader.Load(v));
                    _hotLoaded[v.Dll] = "";
                }
                catch (Exception e)
                {
                    var msg = e.InnerException?.Message ?? e.Message;
                    _hotLoaded[v.Dll] = "Couldn't start: " + msg;
                    ModManagerPlugin.Log.LogError($"Hot-load of {Path.GetFileName(v.Dll)} failed: {e}");
                    Toast?.Invoke($"{m.Name} couldn't start now — it will start after a restart.", false);
                }
            }
            foreach (var d in fresh) _startupDlls.Remove(d);
            if (started.Count > 0)
            {
                ModManagerPlugin.Log.LogMessage("Started without a restart: " + string.Join(", ", started.ToArray()));
                Toast?.Invoke($"{m.Name} is on — no restart needed.", true);
            }
            Refresh();
        }

        // ------------------------------------------------------------------
        // Actions

        public void Disable(ModEntry m)
        {
            if (m.State == ModState.WillEnable) { Pending.Cancel(GameRoot, "enable", m.Rel); Refresh(); return; }
            if (m.InUse || m.Guids.Count > 0)
            {
                Pending.Add(GameRoot, "disable", m.Rel);
                Toast?.Invoke($"{m.Name} turns off when you restart.", true);
            }
            else
            {
                try { Pending.SetEnabled(m.Full, false); }
                catch { Pending.Add(GameRoot, "disable", m.Rel); }
            }
            Refresh();
        }

        public void Enable(ModEntry m)
        {
            if (m.State == ModState.WillDisable) { Pending.Cancel(GameRoot, "disable", m.Rel); Refresh(); return; }
            if (m.InUse)
            {
                // Loaded once this session, then turned off: Mono can't load the same DLL twice.
                Pending.Add(GameRoot, "enable", m.Rel);
                Toast?.Invoke($"{m.Name} turns on when you restart.", true);
                Refresh();
                return;
            }
            try
            {
                Pending.SetEnabled(m.Full, true);
                foreach (var d in m.OffDlls) { var on = d.Substring(0, d.Length - Pending.OffSuffix.Length); _startupDlls.Remove(on); MarkSettled(on); }
            }
            catch { Pending.Add(GameRoot, "enable", m.Rel); }
            Refresh();
            var now = Mods.FirstOrDefault(x => x.Full.Equals(m.Full, StringComparison.OrdinalIgnoreCase));
            if (now != null && now.State == ModState.NeedsRestart && now.Note.StartsWith("Starting")) TryStart(now);
            else if (now != null && now.State == ModState.NeedsRestart) Toast?.Invoke($"{m.Name} needs a restart to turn on ({now.Note}).", false);
        }

        public void Remove(ModEntry m)
        {
            if (m.InUse || m.Guids.Count > 0)
            {
                Pending.Add(GameRoot, "remove", m.Rel);
                Toast?.Invoke($"{m.Name} is removed when you restart.", true);
            }
            else
            {
                try
                {
                    if (m.IsFolder) Directory.Delete(m.Full, true);
                    foreach (var f in new[] { m.Full, m.Full + Pending.OffSuffix }) if (File.Exists(f)) File.Delete(f);
                    Toast?.Invoke($"Removed {m.Name}.", true);
                }
                catch { Pending.Add(GameRoot, "remove", m.Rel); Toast?.Invoke($"{m.Name} is removed when you restart.", true); }
            }
            Refresh();
        }

        public void UndoRemove(ModEntry m) { Pending.Cancel(GameRoot, "remove", m.Rel); Refresh(); }

        // ------------------------------------------------------------------
        // Helpers

        private HotVerdict GetVerdict(string dll)
        {
            if (_verdicts.TryGetValue(dll, out var v)) return v;
            try { v = HotLoader.Check(dll); }
            catch (Exception e) { v = new HotVerdict { Dll = dll, Reason = "Couldn't inspect: " + e.Message }; }
            _verdicts[dll] = v;
            return v;
        }

        /// <summary>A new DLL counts as settled once its size and time haven't changed for two polls.</summary>
        private bool Settled(string dll)
        {
            try
            {
                var fi = new FileInfo(dll);
                var key = (fi.Length, fi.LastWriteTimeUtc);
                if (_settle.TryGetValue(dll, out var s) && s.Size == key.Length && s.Time == key.LastWriteTimeUtc)
                {
                    if (s.Seen >= 1) return (DateTime.UtcNow - key.LastWriteTimeUtc).TotalSeconds > 1.5;
                    _settle[dll] = (s.Size, s.Time, s.Seen + 1);
                    return false;
                }
                _settle[dll] = (key.Length, key.LastWriteTimeUtc, 0);
                _verdicts.Remove(dll);
                return false;
            }
            catch { return false; }
        }

        private void MarkSettled(string dll) => _settle.Remove(dll);

        private void ReadMeta(ModEntry m)
        {
            var dll = m.Dlls.Concat(m.OffDlls).FirstOrDefault(d => GetVerdictName(d) != null);
            if (dll == null) return;
            var meta = _meta[dll];
            m.Name = meta.Name; m.Version = meta.Version;
        }

        /// <summary>Reads the [BepInPlugin] name/version from a DLL that isn't running (cached by file time).</summary>
        private string GetVerdictName(string dll)
        {
            try
            {
                var t = File.GetLastWriteTimeUtc(dll);
                if (_meta.TryGetValue(dll, out var c) && c.Time == t) return c.Name;
                string name = null, ver = "";
                using (var asm = AssemblyDefinition.ReadAssembly(dll, TypeLoader.ReaderParameters))
                {
                    foreach (var type in asm.MainModule.Types)
                    {
                        var a = type.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == "BepInEx.BepInPlugin" && x.ConstructorArguments.Count == 3);
                        if (a == null) continue;
                        name = a.ConstructorArguments[1].Value as string;
                        ver = a.ConstructorArguments[2].Value as string ?? "";
                        break;
                    }
                }
                _meta[dll] = (t, name, ver);
                return name;
            }
            catch { return null; }
        }

        private void ReadDownloaderManifest()
        {
            try
            {
                var f = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NomadModDownloader"), "installed.json");
                if (!File.Exists(f)) { _downloaderManifest = null; return; }
                var t = File.GetLastWriteTimeUtc(f);
                if (_downloaderManifest != null && t == _manifestTime) return;
                _downloaderManifest = (JArray)JObject.Parse(File.ReadAllText(f))["Mods"];
                _manifestTime = t;
            }
            catch { _downloaderManifest = null; }
        }

        private void MatchDownloader(ModEntry m)
        {
            if (_downloaderManifest == null) return;
            foreach (var mod in _downloaderManifest.OfType<JObject>())
            {
                var files = mod["Files"] as JArray;
                if (files == null) continue;
                var hit = files.Select(f => ((string)f ?? "").Replace('\\', '/'))
                               .Any(f => f.Equals(m.Rel, StringComparison.OrdinalIgnoreCase) || f.StartsWith(m.Rel + "/", StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;
                m.NexusId = (int?)mod["ModId"] ?? 0;
                var v = (string)mod["Version"];
                if (!string.IsNullOrEmpty(v) && m.Version.Length == 0) m.Version = v;
                if (m.NexusId > 0 && !string.IsNullOrEmpty(v)) m.Version = v; // compare against what was downloaded, not the DLL's own number
                return;
            }
        }

        private static List<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToList(); }
            catch { return new List<string>(); }
        }

        public static bool IsNewer(string latest, string current)
        {
            if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(current)) return false;
            Version a, b;
            if (TryVer(latest, out a) && TryVer(current, out b)) return a > b;
            return false;
        }

        private static bool TryVer(string s, out Version v)
        {
            v = null;
            var src = s.Trim().TrimStart('v', 'V');
            var n = 0;
            while (n < src.Length && (char.IsDigit(src[n]) || src[n] == '.')) n++;
            s = src.Substring(0, n).Trim('.');
            if (s.Length == 0) return false;
            if (!s.Contains(".")) s += ".0";
            try { v = new Version(s); return true; } catch { return false; }
        }
    }
}
