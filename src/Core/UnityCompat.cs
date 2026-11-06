using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

/// <summary>
/// "Unity Compatibility": Nomad Drive is built with Unity's code stripping, so Unity methods the game itself never
/// calls (AssetBundle.LoadFromFile, XR input, many Physics/Terrain overloads, ...) aren't in its Unity files, and mods
/// that call them fail with MissingMethodException. A BepInEx 6 patcher (mod/UnityCompat, shipped as
/// Assets/UnityCompat.zip) restores them at game start from the full Unity libraries of the same version, which BepInEx
/// hosts at unity.bepinex.dev/libraries. It only adds: the game's own code stays as it is.
/// </summary>
public static class UnityCompat
{
    public const string CompatFolder = @"BepInEx\unity-compat";
    public const string PatcherFolder = "NomadUnityCompat";
    public const string LibsUrl = "https://unity.bepinex.dev/libraries/{0}.zip";

    public static string PatcherDir(GameInstall g) => Path.Combine(g.BepInExDir, "patchers", PatcherFolder);
    public static string PatcherDll(GameInstall g) => Path.Combine(PatcherDir(g), "NomadUnityCompat.dll");
    public static string OutputDir(GameInstall g) => Path.Combine(g.BepInExDir, "unity-compat");
    public static string LibsRoot(GameInstall g) => Path.Combine(g.BepInExDir, "unity-libs");
    public static string? LibsDir(GameInstall g) => UnityVersion(g) is { } v ? Path.Combine(LibsRoot(g), LibsVersion(v)) : null;
    public static string ManagedDir(GameInstall g) => Path.Combine(g.DataFolder, "Managed");

    public static bool PatcherInstalled(GameInstall g) => File.Exists(PatcherDll(g));
    public static bool LibsInstalled(GameInstall g) => LibsDir(g) is { } d && Directory.Exists(d) && Directory.EnumerateFiles(d, "UnityEngine*.dll").Any();
    public static bool SearchPathSet(GameInstall g) => SearchPaths(g).Any(p => p.Equals(CompatFolder, StringComparison.OrdinalIgnoreCase));
    public static bool IsInstalled(GameInstall g) => PatcherInstalled(g) && LibsInstalled(g) && SearchPathSet(g);

    /// <summary>The restored modules exist and were built for this Unity version.</summary>
    public static bool IsBuilt(GameInstall g)
    {
        var m = Path.Combine(OutputDir(g), "manifest.txt");
        if (!File.Exists(m) || UnityVersion(g) is not { } v) return false;
        try { return File.ReadLines(m).FirstOrDefault()?.StartsWith("unity=" + v + ";", StringComparison.Ordinal) == true; }
        catch { return false; }
    }

    public static Version? Bundled()
    {
        try
        {
            using var zip = Open();
            var e = zip.GetEntry("version.txt");
            if (e == null) return null;
            using var r = new StreamReader(e.Open());
            return Version.TryParse(r.ReadToEnd().Trim(), out var v) ? v : null;
        }
        catch { return null; }
    }

    public static Version? Installed(GameInstall g)
    {
        try { return Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(PatcherDll(g)).FileVersion, out var v) ? v : null; }
        catch { return null; }
    }

    public static bool IsOutdated(GameInstall g) => PatcherInstalled(g) && Bundled() is { } b && Installed(g) is { } i && i < b;

    /// <summary>The game's Unity version (e.g. 6000.0.73f1), read the way BepInEx does.</summary>
    public static string? UnityVersion(GameInstall g)
    {
        foreach (var (file, offsets) in new[] { ("globalgamemanagers", new[] { 20, 48 }), ("data.unity3d", new[] { 18 }), ("mainData", new[] { 20 }) })
        {
            var path = Path.Combine(g.DataFolder, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = File.OpenRead(path);
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

    /// <summary>"6000.0.73f1" → "6000.0.73" (BepInEx's library download name).</summary>
    public static string LibsVersion(string unityVersion)
    {
        var m = Regex.Match(unityVersion, @"^(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : unityVersion;
    }

    /// <summary>
    /// Installs the patcher, downloads the matching Unity libraries if they aren't there, and puts the restored-module
    /// folder on doorstop's search path. Returns the files and folders it wrote (relative to the game folder).
    /// </summary>
    public static async Task<List<string>> InstallAsync(HttpClient http, GameInstall game, IProgress<double>? progress, CancellationToken ct)
    {
        var written = new List<string>();
        written.AddRange(InstallPatcher(game));
        if (!LibsInstalled(game))
        {
            var v = UnityVersion(game) ?? throw new InvalidOperationException(L.T("Couldn't tell which Unity version Nomad Drive uses."));
            var lv = LibsVersion(v);
            var zip = await NexusApi.DownloadFileAsync(http, new Uri(string.Format(LibsUrl, lv)), AppPaths.Downloads, "unity-libs-" + lv, progress, ct);
            var dir = Path.Combine(LibsRoot(game), lv);
            Directory.CreateDirectory(dir);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var dlls = archive.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains("..")).ToList();
                if (!dlls.Any(e => e.Name.Equals("UnityEngine.CoreModule.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(L.T("The downloaded Unity libraries look wrong. Nothing was installed."));
                foreach (var e in dlls) e.ExtractToFile(Path.Combine(dir, e.Name), true);
            }
            written.Add(Path.GetRelativePath(game.Root, dir).Replace('\\', '/') + "/");
        }
        if (EnsureSearchPath(game)) written.Add("doorstop_config.ini");
        return written;
    }

    /// <summary>Just the patcher (shipped inside the app).</summary>
    public static List<string> InstallPatcher(GameInstall game)
    {
        var written = new List<string>();
        using var zip = Open();
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith("/") || e.Name.Length == 0 || e.Name == "version.txt") continue;
            var rel = ModInstaller.Norm(e.FullName);
            if (!rel.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = ModInstaller.SafeJoin(game.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
            written.Add(rel);
        }
        return written;
    }

    /// <summary>Adds BepInEx\unity-compat to dll_search_path_override, after the .NET fix and before BepInEx\core.</summary>
    public static bool EnsureSearchPath(GameInstall game)
    {
        var ini = BepInExChecker.DoorstopIni(game);
        if (!File.Exists(ini)) return false;
        var text = File.ReadAllText(ini);
        var paths = SearchPaths(game);
        if (paths.Any(p => p.Equals(CompatFolder, StringComparison.OrdinalIgnoreCase))) return false;
        var at = paths.FindIndex(p => p.Equals(BepInExChecker.DotNetFixFolder, StringComparison.OrdinalIgnoreCase)) + 1;
        paths.Insert(at, CompatFolder);
        var value = "\"" + string.Join(";", paths) + "\"";
        if (Regex.IsMatch(text, @"(?im)^\s*dll_search_path_override\s*="))
            text = Regex.Replace(text, @"(?im)^(\s*dll_search_path_override\s*=).*$", m => m.Groups[1].Value + " " + value);
        else if (Regex.IsMatch(text, @"(?im)^\[UnityMono\]"))
            text = Regex.Replace(text, @"(?im)^\[UnityMono\]\s*$", m => "[UnityMono]\r\n\r\ndll_search_path_override = " + value);
        else
            text = text.TrimEnd() + "\r\n\r\n[UnityMono]\r\n\r\ndll_search_path_override = " + value + "\r\n";
        File.WriteAllText(ini, text);
        return true;
    }

    public static void Remove(GameInstall game)
    {
        try { if (Directory.Exists(PatcherDir(game))) Directory.Delete(PatcherDir(game), true); } catch { }
        try { if (Directory.Exists(OutputDir(game))) Directory.Delete(OutputDir(game), true); } catch { }
        var ini = BepInExChecker.DoorstopIni(game);
        if (!File.Exists(ini) || !SearchPathSet(game)) return;
        var paths = SearchPaths(game).Where(p => !p.Equals(CompatFolder, StringComparison.OrdinalIgnoreCase)).ToList();
        var value = paths.Count > 1 ? "\"" + string.Join(";", paths) + "\"" : string.Join(";", paths);
        File.WriteAllText(ini, Regex.Replace(File.ReadAllText(ini), @"(?im)^(\s*dll_search_path_override\s*=).*$", m => m.Groups[1].Value + " " + value));
    }

    private static List<string> SearchPaths(GameInstall g)
    {
        try
        {
            var ini = BepInExChecker.DoorstopIni(g);
            if (!File.Exists(ini)) return new List<string>();
            var m = Regex.Match(File.ReadAllText(ini), @"(?im)^\s*dll_search_path_override\s*=(.*)$");
            return m.Success
                ? m.Groups[1].Value.Trim().Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Replace('/', '\\')).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    // ---------------- what mods need ----------------

    public sealed record ModNeed(string Mod, string Dll, List<GameApiScanner.Missing> Restorable, List<GameApiScanner.Missing> Unavailable);

    /// <summary>
    /// For each enabled mod: the game members it uses that the game's files don't have, split into what Unity
    /// Compatibility restores and what no fix can add (the game itself changed, or another Unity version).
    /// </summary>
    public static List<ModNeed> ScanMods(GameInstall game, IEnumerable<string>? onlyDlls = null)
    {
        var result = new List<ModNeed>();
        var managed = ManagedDir(game);
        if (!Directory.Exists(managed)) return result;
        var dlls = onlyDlls?.ToList() ?? (Directory.Exists(game.PluginsDir)
            ? Directory.EnumerateFiles(game.PluginsDir, "*.dll", SearchOption.AllDirectories).Where(d => !BepInExCompat.IsCompatPlugin(game, d)).ToList()
            : new List<string>());
        if (dlls.Count == 0) return result;
        using var gameApi = new GameApiScanner.GameApi(managed);
        using var fullApi = LibsInstalled(game) ? new GameApiScanner.GameApi(managed, LibsDir(game)!) : null;
        foreach (var dll in dlls)
        {
            List<GameApiScanner.Missing> missing;
            try { missing = GameApiScanner.Scan(dll, gameApi); }
            catch { continue; }
            if (missing.Count == 0) continue;
            var restorable = new List<GameApiScanner.Missing>();
            var unavailable = new List<GameApiScanner.Missing>();
            HashSet<string>? stillMissing = null;
            if (fullApi != null)
            {
                try { stillMissing = GameApiScanner.Scan(dll, fullApi).Select(m => m.Assembly + "|" + m.Display).ToHashSet(); }
                catch { }
            }
            foreach (var m in missing)
            {
                var fixable = m.IsUnity && (stillMissing == null || !stillMissing.Contains(m.Assembly + "|" + m.Display));
                (fixable ? restorable : unavailable).Add(m);
            }
            var name = PluginInspector.Read(dll).Select(p => p.Name).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(dll);
            result.Add(new ModNeed(name, dll, restorable, unavailable));
        }
        return result;
    }

    private static ZipArchive Open() =>
        new(typeof(UnityCompat).Assembly.GetManifestResourceStream("Assets.UnityCompat.zip")
            ?? throw new InvalidOperationException("The app is missing its Unity Compatibility files."), ZipArchiveMode.Read);
}
