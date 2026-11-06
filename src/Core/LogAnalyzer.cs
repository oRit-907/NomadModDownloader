using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

public enum ProblemLevel { Info, Warning, Error, Crash }

/// <summary>One thing that went wrong in the last game session, in plain words.</summary>
public sealed class Problem
{
    public ProblemLevel Level { get; init; }
    public string Title { get; init; } = "";       // short, translated
    public string Detail { get; init; } = "";      // the original log text (first lines)
    public string? Plugin { get; init; }           // plugin name as BepInEx prints it
    public string? ModName { get; set; }           // installed mod it belongs to, when known
    public int Count { get; set; } = 1;
    /// <summary>Plugin guessed from a stack trace: dropped if it doesn't match an installed plugin.</summary>
    internal bool Guessed { get; init; }
}

/// <summary>The result of reading the last session's logs.</summary>
public sealed class SessionReport
{
    public List<Problem> Problems { get; } = new();
    public List<string> LoadedPlugins { get; } = new();
    public DateTime? LogTime { get; set; }
    public bool BepInExRan { get; set; }
    public bool ChainloaderFinished { get; set; }
    public string? LogPath { get; set; }
}

/// <summary>
/// Reads BepInEx's LogOutput.log (and Unity's Player.log) from the last game session and turns
/// errors into a short list: which mod failed, and why, where that can be told.
/// </summary>
public static class LogAnalyzer
{
    private static readonly Regex Line = new(@"^\[(Fatal|Error|Warning|Message|Info|Debug)\s*:\s*(.*?)\]\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex Loading = new(@"^Loading \[(.+?) ([^ \]]+)\]", RegexOptions.Compiled);
    private static readonly Regex MissingDeps = new(@"Could not load \[(.+?)(?: [^ \]]+)?\] because it has missing dependencies: (.+)$", RegexOptions.Compiled);
    private static readonly Regex Incompatible = new(@"Could not load \[(.+?)(?: [^ \]]+)?\] because it is incompatible with: (.+)$", RegexOptions.Compiled);
    private static readonly Regex Newer = new(@"Skipping \[(.+?)(?: [^ \]]+)?\] because a newer version exists \((.+)\)", RegexOptions.Compiled);
    private static readonly Regex ErrorLoading = new(@"Error loading \[(.+?)(?: [^ \]]+)?\]\s*:?\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s*(?:at\s+)?([A-Za-z_][\w`]*)\.[\w`.<>]+\s*\(", RegexOptions.Compiled);

    public static string BepInExLog(GameInstall game) => Path.Combine(game.BepInExDir, "LogOutput.log");

    /// <summary>Unity's own log for the game: %USERPROFILE%\AppData\LocalLow\Indie Devil\Nomad Drive\Player.log.</summary>
    public static string? PlayerLog() => SaveBackups.FindSaveFolder() is { } dir && File.Exists(Path.Combine(dir, "Player.log")) ? Path.Combine(dir, "Player.log") : null;

    public static SessionReport Analyze(GameInstall game, Manifest manifest)
    {
        var path = BepInExLog(game);
        var report = new SessionReport { LogPath = path };
        if (File.Exists(path))
        {
            report.BepInExRan = true;
            report.LogTime = File.GetLastWriteTime(path);
            AnalyzeText(ReadShared(path), report);
        }
        var player = PlayerLog();
        if (player != null)
        {
            try
            {
                var text = ReadShared(player);
                if (text.Contains("Crash!!!") || text.Contains("Received signal SIGSEGV") || text.Contains("access violation", StringComparison.OrdinalIgnoreCase))
                    report.Problems.Insert(0, new Problem { Level = ProblemLevel.Crash, Title = L.T("The game crashed last time it ran."), Detail = Tail(text, 12) });
            }
            catch { }
        }
        Attribute(report, game, manifest);
        return report;
    }

    /// <summary>The parsing step on its own (used by tests).</summary>
    public static void AnalyzeText(string text, SessionReport report)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var byKey = new Dictionary<string, Problem>();

        void Add(Problem p)
        {
            var key = p.Level + "|" + p.Plugin + "|" + p.Title + "|" + FirstLine(p.Detail);
            if (byKey.TryGetValue(key, out var have)) { have.Count++; return; }
            byKey[key] = p;
            report.Problems.Add(p);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var m = Line.Match(lines[i]);
            if (!m.Success) continue;
            var level = m.Groups[1].Value;
            var source = m.Groups[2].Value.Trim();
            var msg = m.Groups[3].Value.Trim();

            // Continuation lines (stack traces) until the next [Level : Source] line.
            var detail = new List<string> { msg };
            var j = i + 1;
            while (j < lines.Length && !Line.IsMatch(lines[j])) { if (lines[j].Trim().Length > 0) detail.Add(lines[j].TrimEnd()); j++; }

            if (source == "BepInEx")
            {
                if (Loading.Match(msg) is { Success: true } lm) { report.LoadedPlugins.Add(lm.Groups[1].Value); continue; }
                if (msg.StartsWith("Chainloader startup complete", StringComparison.OrdinalIgnoreCase)) { report.ChainloaderFinished = true; continue; }
                if (MissingDeps.Match(msg) is { Success: true } md)
                {
                    Add(new Problem { Level = ProblemLevel.Error, Plugin = md.Groups[1].Value, Title = L.F("Didn't load: it needs another mod that isn't installed ({0}).", md.Groups[2].Value.Trim()), Detail = msg });
                    continue;
                }
                if (Incompatible.Match(msg) is { Success: true } im)
                {
                    Add(new Problem { Level = ProblemLevel.Error, Plugin = im.Groups[1].Value, Title = L.F("Didn't load: it doesn't work together with {0}.", im.Groups[2].Value.Trim()), Detail = msg });
                    continue;
                }
                if (Newer.Match(msg) is { Success: true } nm)
                {
                    Add(new Problem { Level = ProblemLevel.Warning, Plugin = nm.Groups[1].Value, Title = L.F("Installed twice: the newer copy ({0}) was used instead.", nm.Groups[2].Value.Trim()), Detail = msg });
                    continue;
                }
                if (ErrorLoading.Match(msg) is { Success: true } em && level is "Error" or "Fatal")
                {
                    Add(new Problem { Level = ProblemLevel.Error, Plugin = em.Groups[1].Value, Title = L.T("Failed to start (an error while loading)."), Detail = Join(detail) });
                    continue;
                }
            }

            if (level is not ("Error" or "Fatal")) continue;
            if (IsHarmless(source, msg)) continue;
            var plugin = source is "BepInEx" or "Unity Log" or "HarmonyX" or "Harmony" or "Preloader" ? null : source;
            var title = source.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)
                ? L.T("A mod couldn't change the game's code (probably not updated for this game version).")
                : msg.Contains("Exception", StringComparison.Ordinal) ? L.T("A mod hit an error while the game was running.") : L.T("A mod reported an error.");
            // Unity errors: guess the mod from the stack trace's first non-game namespace.
            string? frame = null;
            foreach (var d in detail.Skip(1))
                if (Frame.Match(d) is { Success: true } fm && !IsGameOrSystem(fm.Groups[1].Value)) { frame = fm.Groups[1].Value; break; }
            Add(new Problem { Level = level == "Fatal" ? ProblemLevel.Crash : ProblemLevel.Error, Plugin = plugin ?? frame, Guessed = plugin == null && frame != null, Title = title, Detail = Join(detail) });
        }

        if (report.LoadedPlugins.Count > 0 && !report.ChainloaderFinished)
            report.Problems.Insert(0, new Problem { Level = ProblemLevel.Crash, Title = L.T("BepInEx didn't finish loading mods: the game probably closed or crashed while starting."), Detail = "" });
    }

    /// <summary>
    /// Errors BepInEx itself logs that don't affect mods. "Unable to start Unity log writer": on Unity 6, BepInEx 6
    /// can't copy its messages into Unity's own Player.log at startup. Mods still load and LogOutput.log is complete.
    /// </summary>
    public static bool IsHarmless(string source, string msg) =>
        source == "BepInEx" && msg.StartsWith("Unable to start Unity log writer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Links each problem to an installed mod: by plugin name, plugin id, or DLL / folder name.</summary>
    private static void Attribute(SessionReport report, GameInstall game, Manifest manifest)
    {
        if (report.Problems.All(p => p.Plugin == null)) return;
        var plugins = PluginInspector.Scan(game, includeDisabled: true);
        foreach (var p in report.Problems.Where(p => p.Plugin != null).ToList())
        {
            var key = p.Plugin!;
            var info = plugins.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || x.Guid.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                   || Path.GetFileNameWithoutExtension(x.File.Replace(".disabled", "")).Equals(key, StringComparison.OrdinalIgnoreCase));
            string? rel = info != null ? Path.GetRelativePath(game.Root, info.File).Replace('\\', '/').Replace(".dll.disabled", ".dll") : null;
            var mod = rel == null ? null : manifest.Mods.FirstOrDefault(m => m.Files.Any(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase)));
            mod ??= manifest.Mods.FirstOrDefault(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || ModInstaller.ModFolderName(m.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
            p.ModName = mod?.Name ?? info?.Name;
            if (p.Guessed && p.ModName == null)
            {
                // The trace's namespace wasn't a mod (probably the game's own code): don't blame anyone.
                var i = report.Problems.IndexOf(p);
                report.Problems[i] = new Problem { Level = p.Level, Title = p.Title, Detail = p.Detail, Count = p.Count };
            }
        }
    }

    private static bool IsGameOrSystem(string ns) =>
        ns is "System" or "UnityEngine" or "Unity" or "Mono" or "HarmonyLib" or "BepInEx" or "MonoMod" or "TMPro" or "DMD" or "Cinemachine" or "Rewired" or "NWH"
        || ns.StartsWith("(wrapper", StringComparison.Ordinal) || ns.StartsWith("DMD<", StringComparison.Ordinal);

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static string Join(List<string> lines) => string.Join("\n", lines.Take(8)) + (lines.Count > 8 ? "\n…" : "");
    private static string FirstLine(string s) { var i = s.IndexOf('\n'); return i < 0 ? s : s[..i]; }
    private static string Tail(string text, int n) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).TakeLast(n));
}
