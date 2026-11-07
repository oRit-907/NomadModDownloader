using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    internal sealed class NexusMod
    {
        public int Id;
        public string Name = "";
        public string Summary = "";
        public string Version = "";
        public string Author = "";
        public long Downloads;
        public long Endorsements;
        public string Thumb = "";
        public bool Hidden;            // not something to install: nobody has downloaded it, or its files are only programs (.exe)
        public string Category = "";   // our own category (Nexus files every Nomad Drive mod as Miscellaneous)
        public string PageUrl => $"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{Id}";
    }

    internal sealed class NexusFile
    {
        public int Id;
        public string Name = "", Version = "", Category = "", Description = "";
        public DateTime Date;
        public long Size;
        public bool Primary;
        public List<string> Changelog = new List<string>();
        public bool IsCurrent => Category == "MAIN" || Category == "OPTIONAL" || Category == "UPDATE" || Category == "MISCELLANEOUS";
    }

    internal sealed class NexusRequirement
    {
        public int ModId;            // 0 = not a Nomad Drive mod on Nexus
        public string Name = "", Url = "", Notes = "";
        public bool External;
    }

    /// <summary>A mod's full page: description, files (with changelogs) and requirements.</summary>
    internal sealed class NexusDetails
    {
        public NexusMod Mod;
        public string Description = "";
        public string Picture = "";
        public DateTime Updated;
        public readonly List<NexusFile> Files = new List<NexusFile>();
        public readonly List<NexusRequirement> Requirements = new List<NexusRequirement>();
    }

    /// <summary>Browses Nexus with the public v2 GraphQL API (no login needed), Nomad Drive only.</summary>
    internal static class Nexus
    {
        public const string GameDomain = "nomaddrive";
        public const int GameId = 9724;
        public const int DownloaderModId = 51;
        private const string Url = "https://api.nexusmods.com/v2/graphql";
        private const string Fields = "modId name summary version author downloads endorsements thumbnailUrl modCategory { name }";

        public static readonly string[] SortNames = { "Updated", "Downloads", "Endorsed", "Newest" };
        private static readonly string[] SortKeys = { "updatedAt", "downloads", "endorsements", "createdAt" };

        private static readonly HashSet<string> ProgramExt = new HashSet<string> { "exe", "msi" };
        private static readonly HashSet<string> OldCategories = new HashSet<string> { "ARCHIVED", "OLD_VERSION", "REMOVED", "DELETED" };
        private static readonly Dictionary<string, Texture2D> Thumbs = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> ThumbLoading = new HashSet<string>();

        public static IEnumerator Search(string text, int sort, int offset, int count, Action<List<NexusMod>, int, string> done)
        {
            var filter = new JObject { ["gameDomainName"] = new JArray(new JObject { ["value"] = GameDomain, ["op"] = "EQUALS" }) };
            if (!string.IsNullOrEmpty(text?.Trim()))
                filter["name"] = new JArray(new JObject { ["value"] = text.Trim(), ["op"] = "WILDCARD" });
            var sortArr = new JArray(new JObject { [SortKeys[Mathf.Clamp(sort, 0, 3)]] = new JObject { ["direction"] = "DESC" } });
            var vars = new JObject { ["f"] = filter, ["s"] = sortArr, ["o"] = offset, ["c"] = count };
            const string q = "query($f: ModsFilter, $s: [ModsSort!], $o: Int, $c: Int) { mods(filter: $f, sort: $s, offset: $o, count: $c) { totalCount nodes { " + Fields + " } } }";

            JToken data = null; string err = null;
            yield return Query(q, vars, d => data = d, e => err = e);
            if (err != null) { done(null, 0, err); yield break; }
            var list = (data?["mods"]?["nodes"] as JArray ?? new JArray()).Select(Parse).ToList();
            foreach (var m in list) if (m.Downloads == 0) m.Hidden = true;

            // Mods whose current files are all programs (.exe/.msi): one request for the whole page.
            var check = list.Where(m => !m.Hidden).Select(m => m.Id).ToList();
            if (check.Count > 0)
            {
                var sb = new StringBuilder("query {");
                foreach (var id in check) sb.Append($" m{id}: modFiles(modId: {id}, gameId: {GameId}) {{ category detectedFileExtension }}");
                sb.Append(" }");
                JToken files = null;
                yield return Query(sb.ToString(), null, d => files = d, e => { });
                if (files is JObject fo)
                    foreach (var m in list)
                    {
                        if (!(fo["m" + m.Id] is JArray arr)) continue;
                        var current = arr.Where(f => !OldCategories.Contains(((string)f["category"] ?? "").ToUpperInvariant())).ToList();
                        if (current.Count > 0 && current.All(f => ProgramExt.Contains(((string)f["detectedFileExtension"] ?? "").ToLowerInvariant()))) m.Hidden = true;
                    }
            }
            done(list, (int?)data?["mods"]?["totalCount"] ?? list.Count, null);
        }

        /// <summary>Latest version and author of several mods in one request.</summary>
        public static IEnumerator Lookup(IEnumerable<int> ids, Action<List<NexusMod>> done)
        {
            var list = ids.Distinct().Take(40).ToList();
            if (list.Count == 0) { done(new List<NexusMod>()); yield break; }
            var sb = new StringBuilder("query {");
            foreach (var id in list) sb.Append($" m{id}: mod(modId: {id}, gameId: {GameId}) {{ {Fields} }}");
            sb.Append(" }");
            JToken data = null;
            yield return Query(sb.ToString(), null, d => data = d, e => ModManagerPlugin.Log.LogWarning("Update check failed: " + e));
            var result = new List<NexusMod>();
            if (data is JObject o) foreach (var p in o.Properties()) if (p.Value is JObject) result.Add(Parse(p.Value));
            done(result);
        }

        /// <summary>Everything on a mod's page, in one request.</summary>
        public static IEnumerator Details(int id, Action<NexusDetails, string> done)
        {
            var q = $"query {{ mod(modId: {id}, gameId: {GameId}) {{ {Fields} description pictureUrl updatedAt modRequirements {{ nexusRequirements {{ nodes {{ modId modName gameId externalRequirement url notes }} }} }} }} "
                    + $"modFiles(modId: {id}, gameId: {GameId}) {{ fileId name version category date description changelogText primary sizeInBytes }} }}";
            JToken data = null; string err = null;
            yield return Query(q, null, d => data = d, e => err = e);
            if (err != null || data?["mod"] == null) { done(null, err ?? "Nexus didn't send the mod's page"); yield break; }
            var d2 = new NexusDetails { Mod = Parse(data["mod"]) };
            d2.Description = BbText(Html((string)data["mod"]["description"] ?? ""));
            d2.Picture = (string)data["mod"]["pictureUrl"] ?? "";
            if (DateTime.TryParse((string)data["mod"]["updatedAt"], out var up)) d2.Updated = up;
            foreach (var n in data["mod"]["modRequirements"]?["nexusRequirements"]?["nodes"] as JArray ?? new JArray())
            {
                var ext = (bool?)n["externalRequirement"] ?? false;
                var game = (string)n["gameId"];
                d2.Requirements.Add(new NexusRequirement
                {
                    ModId = !ext && (game == null || game == GameId.ToString()) ? int.TryParse((string)n["modId"], out var mid) ? mid : 0 : 0,
                    Name = Html((string)n["modName"] ?? ""), Url = (string)n["url"] ?? "", Notes = Html((string)n["notes"] ?? ""), External = ext,
                });
            }
            foreach (var f in data["modFiles"] as JArray ?? new JArray())
            {
                var file = new NexusFile
                {
                    Id = (int?)f["fileId"] ?? 0,
                    Name = Html((string)f["name"] ?? ""), Version = (string)f["version"] ?? "", Category = ((string)f["category"] ?? "").ToUpperInvariant(),
                    Description = BbText(Html((string)f["description"] ?? "")), Size = (long?)f["sizeInBytes"] ?? 0, Primary = (bool?)f["primary"] ?? false,
                };
                var secs = (long?)f["date"] ?? 0;
                file.Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs).ToLocalTime();
                if (f["changelogText"] is JArray cl) file.Changelog.AddRange(cl.Select(x => BbText(Html((string)x ?? ""))).Where(x => x.Length > 0));
                d2.Files.Add(file);
            }
            d2.Files.Sort((a, b) => b.IsCurrent != a.IsCurrent ? (b.IsCurrent ? 1 : -1) : b.Date.CompareTo(a.Date));
            done(d2, null);
        }

        /// <summary>Nexus descriptions are BBCode with &lt;br /&gt; line breaks: turn them into plain text with bullets.</summary>
        public static string BbText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "");
            if (s.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0) s = s.Replace("\n", ""); // the <br />s are the line breaks
            s = s.Replace("<br />", "\n").Replace("<br/>", "\n").Replace("<br>", "\n");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[img[^\]]*\].*?\[/img\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[youtube\].*?\[/youtube\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[\*\]", "\n•  ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/\*\]", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?list[^\]]*\]", "\n");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[line\]", "\n――――――\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?(heading|center|quote|spoiler|code)[^\]]*\]", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z]+(=[^\]]*)?\]", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", "");
            var lines = s.Split('\n').Select(l => l.Trim()).ToList();
            var sb = new StringBuilder();
            var blank = 0;
            foreach (var l in lines)
            {
                if (l.Length == 0 || l == "•") { if (++blank == 1 && sb.Length > 0) sb.Append('\n'); continue; }
                blank = 0;
                sb.Append(l).Append('\n');
            }
            return sb.ToString().Trim();
        }

        private static IEnumerator Query(string query, JObject vars, Action<JToken> ok, Action<string> fail)
        {
            var body = new JObject { ["query"] = query };
            if (vars != null) body["variables"] = vars;
            using (var req = new UnityWebRequest(Url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None)));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Application-Name", "NomadModManager");
                req.SetRequestHeader("Application-Version", ModManagerPlugin.Version);
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { fail("Couldn't reach Nexus Mods (" + req.error + ")"); yield break; }
                JObject root;
                try { root = JObject.Parse(req.downloadHandler.text); }
                catch { fail("Nexus sent something unexpected"); yield break; }
                if (root["errors"] is JArray errs && errs.Count > 0) { fail("Nexus: " + (string)errs[0]["message"]); yield break; }
                ok(root["data"]);
            }
        }

        private static NexusMod Parse(JToken n) => Classified(ParseRaw(n));

        private static NexusMod ParseRaw(JToken n) => new NexusMod
        {
            Id = (int?)n["modId"] ?? 0,
            Name = Html((string)n["name"] ?? ""),
            Summary = Html((string)n["summary"] ?? "").Replace("<br />", " ").Replace("\n", " ").Trim(),
            Version = (string)n["version"] ?? "",
            Author = (string)n["author"] ?? "",
            Downloads = (long?)n["downloads"] ?? 0,
            Endorsements = (long?)n["endorsements"] ?? 0,
            Thumb = (string)n["thumbnailUrl"] ?? ""
        };

        private static NexusMod Classified(NexusMod m) { m.Category = Categories.Classify(m.Name, m.Summary); return m; }

        /// <summary>Decodes the HTML entities Nexus uses in names and summaries (System.Net.WebUtility is stripped from the game).</summary>
        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var semi = s[i] == '&' ? s.IndexOf(';', i) : -1;
                if (semi > i && semi - i <= 10)
                {
                    var ent = s.Substring(i + 1, semi - i - 1);
                    string rep = null;
                    switch (ent)
                    {
                        case "amp": rep = "&"; break;
                        case "lt": rep = "<"; break;
                        case "gt": rep = ">"; break;
                        case "quot": rep = "\""; break;
                        case "apos": rep = "'"; break;
                        case "nbsp": rep = " "; break;
                        default:
                            if (ent.StartsWith("#x") && int.TryParse(ent.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var hx)) rep = char.ConvertFromUtf32(hx);
                            else if (ent.StartsWith("#") && int.TryParse(ent.Substring(1), out var dc)) rep = char.ConvertFromUtf32(dc);
                            break;
                    }
                    if (rep != null) { sb.Append(rep); i = semi; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>The mod's thumbnail, or null while it loads (or if it's a format Unity can't read).</summary>
        public static Texture2D Thumb(MonoBehaviour host, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (Thumbs.TryGetValue(url, out var t)) return t;
            if (ThumbLoading.Add(url)) host.StartCoroutine(LoadThumb(url));
            return null;
        }

        private static IEnumerator LoadThumb(string url)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 20;
                yield return req.SendWebRequest();
                Texture2D tex = null;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var data = req.downloadHandler.data;
                    if (WebP.IsWebP(data)) tex = WebP.Decode(data); // Nexus serves WebP
                    else
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!tex.LoadImage(data)) { UnityEngine.Object.Destroy(tex); tex = null; }
                    }
                    if (tex != null) { tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear; UnityEngine.Object.DontDestroyOnLoad(tex); }
                }
                Thumbs[url] = tex;
            }
        }
    }

    /// <summary>Hands installs to Nomad Mod Downloader, which does the Nexus download the way Nexus requires.</summary>
    internal static class Downloader
    {
        /// <summary>First version that understands "--game-install".</summary>
        public static readonly Version MinVersion = new Version(2, 4, 0);
        private static string _cached;
        private static float _nextSearch;

        private static string RegFile => Path.Combine(Pending.Dir(Paths.GameRootPath), "downloader.txt");

        /// <summary>
        /// Where the downloader is: the path it registered, or a look through the usual places
        /// (Desktop, Downloads, Documents, Programs). Remembered once found.
        /// </summary>
        public static string ExePath
        {
            get
            {
                if (_cached != null && File.Exists(_cached)) return _cached;
                try
                {
                    if (File.Exists(RegFile))
                    {
                        var p = File.ReadAllText(RegFile).Trim();
                        if (p.Length > 0 && File.Exists(p)) return _cached = p;
                    }
                }
                catch { }
                if (Time.realtimeSinceStartup < _nextSearch) return null;
                _nextSearch = Time.realtimeSinceStartup + 30f;
                var found = Search(); // (Process.GetProcessesByName is stripped from the game)
                if (found != null)
                {
                    _cached = found;
                    try { Directory.CreateDirectory(Pending.Dir(Paths.GameRootPath)); File.WriteAllText(RegFile, found); } catch { }
                    ModManagerPlugin.Log.LogInfo("Found Nomad Mod Downloader at " + found);
                }
                return found;
            }
        }

        public static bool Available => ExePath != null;

        /// <summary>Remembers where the app is (it does this itself on start, too).</summary>
        public static void Register(string exe)
        {
            _cached = exe;
            try { Directory.CreateDirectory(Pending.Dir(Paths.GameRootPath)); File.WriteAllText(RegFile, exe); } catch { }
        }

        public static Version VersionOf(string exe)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (v.FileMajorPart > 0 || v.FileMinorPart > 0) return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { }
            // The Microsoft Store build is started through an alias (no version in it): the app writes its version next to downloader.txt.
            try
            {
                var f = Path.Combine(Pending.Dir(Paths.GameRootPath), "downloader-version.txt");
                if (exe.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 && File.Exists(f)) return new Version(File.ReadAllText(f).Trim());
            }
            catch { }
            return null;
        }

        /// <summary>The downloader is there but too old to open a mod for the game.</summary>
        public static bool IsOutdated(out Version have)
        {
            have = null;
            var exe = ExePath;
            if (exe == null) return false;
            have = VersionOf(exe);
            return have != null && have < MinVersion;
        }

        private static string Search()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var roots = new List<string>();
            foreach (var r in new[]
            {
                Path.Combine(home, "Desktop"), Path.Combine(home, "Downloads"), Path.Combine(home, "Documents"),
                Path.Combine(home, "OneDrive\\Desktop"), Path.Combine(home, "OneDrive\\Documents"),
                Path.Combine(local, "Programs"), Path.Combine(Path.Combine(local, "Programs"), "NomadModDownloader"), Environment.GetEnvironmentVariable("ProgramFiles") ?? "", Paths.GameRootPath
            })
                if (r.Length > 0 && Directory.Exists(r)) roots.Add(r);

            // The Microsoft Store build's alias
            var alias = Path.Combine(Path.Combine(Path.Combine(local, "Microsoft"), "WindowsApps"), "NomadModDownloader.exe");
            if (local.Length > 0 && File.Exists(alias)) return alias;

            string best = null; Version bestV = null;
            foreach (var root in roots)
            {
                foreach (var exe in FindIn(root, 2))
                {
                    var v = VersionOf(exe) ?? new Version(0, 0);
                    if (best == null || v > bestV) { best = exe; bestV = v; }
                }
            }
            return best;
        }

        private static IEnumerable<string> FindIn(string dir, int depth)
        {
            string[] files = new string[0], dirs = new string[0];
            try { files = Directory.GetFiles(dir, "NomadModDownloader*.exe"); } catch { }
            foreach (var f in files)
            {
                var n = Path.GetFileName(f);
                if (n.EndsWith(".old.exe", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".new.exe", StringComparison.OrdinalIgnoreCase)) continue;
                yield return f;
            }
            if (depth <= 0) yield break;
            try { dirs = Directory.GetDirectories(dir); } catch { }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var f in FindIn(d, depth - 1)) yield return f;
            }
        }

        /// <summary>Brings up the downloader (starting it if needed).</summary>
        public static bool Show()
        {
            var exe = ExePath;
            if (exe == null) return false;
            try { Process.Start(new ProcessStartInfo(exe, "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); return true; }
            catch { return false; }
        }

        /// <summary>First version that takes several mods, a specific file ("mod:file") and "--game-check".</summary>
        public static readonly Version QueueVersion = new Version(2, 6, 8);

        /// <summary>The downloader can take several mods at once, a specific version, and open Check BepInEx.</summary>
        public static bool CanQueue
        {
            get
            {
                // Checked while drawing: cache it (reading the exe's version every frame is slow).
                if (Time.realtimeSinceStartup < _canQueueUntil) return _canQueue;
                _canQueueUntil = Time.realtimeSinceStartup + 10f;
                return _canQueue = ExePath is string e && VersionOf(e) is Version v && v >= QueueVersion;
            }
        }
        private static bool _canQueue;
        private static float _canQueueUntil;

        /// <summary>Opens the mod in the downloader (starting it if needed). Returns false if the downloader isn't installed.</summary>
        public static bool Install(int modId) => Run("--game-install " + modId, $"install mod {modId}");

        /// <summary>A specific file (version) of a mod. Needs <see cref="CanQueue"/>.</summary>
        public static bool InstallFile(int modId, int fileId) => Run($"--game-install {modId}:{fileId}", $"install file {fileId} of mod {modId}");

        /// <summary>Several mods one after the other (Update all, a profile's missing mods). Needs <see cref="CanQueue"/>.</summary>
        public static bool InstallMany(IEnumerable<int> modIds)
        {
            var ids = modIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return true;
            if (ids.Count == 1) return Install(ids[0]);
            return Run("--game-install " + string.Join(",", ids.Select(i => i.ToString()).ToArray()), $"install {ids.Count} mods");
        }

        /// <summary>Opens the downloader on Check BepInEx (older versions just open).</summary>
        public static bool ShowCheck() => CanQueue ? Run("--game-check", "open Check BepInEx") : Show();

        private static bool Run(string args, string what)
        {
            var exe = ExePath;
            if (exe == null) return false;
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                ModManagerPlugin.Log.LogInfo($"Asked Nomad Mod Downloader to {what}.");
                return true;
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log.LogWarning("Couldn't start Nomad Mod Downloader: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>Quits and starts the game again through Steam, so queued changes are applied at launch.</summary>
    internal static class Restarter
    {
        public static void Restart()
        {
            var pid = Process.GetCurrentProcess().Id;
            var appId = SteamAppId();
            var target = appId != null ? "steam://rungameid/" + appId : Paths.ExecutablePath;
            var script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process '{target.Replace("'", "''")}'";
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command \"" + script + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log.LogError("Couldn't set up the relaunch: " + e.Message);
            }
            ModManagerPlugin.Log.LogMessage("Restarting to apply mod changes.");
            Application.Quit();
        }

        private static string SteamAppId()
        {
            var env = Environment.GetEnvironmentVariable("SteamAppId") ?? Environment.GetEnvironmentVariable("SteamGameId");
            if (!string.IsNullOrEmpty(env) && env != "0") return env;
            try
            {
                var t = Type.GetType("Steamworks.SteamClient, Facepunch.Steamworks.Win64");
                var id = t?.GetProperty("AppId")?.GetValue(null, null);
                var v = id?.GetType().GetField("Value")?.GetValue(id);
                if (v != null && v.ToString() != "0") return v.ToString();
            }
            catch { }
            try
            {
                var f = Path.Combine(Paths.GameRootPath, "steam_appid.txt");
                if (File.Exists(f)) return File.ReadAllText(f).Trim();
            }
            catch { }
            return null;
        }
    }
}
