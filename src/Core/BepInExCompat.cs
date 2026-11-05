using System.IO.Compression;

namespace NomadModDownloader.Core;

/// <summary>
/// "BepInEx 5 Compatibility": a BepInEx 6 plugin (mod/BepInEx5Compat) that runs BepInEx 5's own runtime beside
/// BepInEx 6, so mods built for BepInEx 5 still load. Shipped inside the app (Assets/BepInEx5Compat.zip) and set up
/// with every BepInEx 6 install.
/// </summary>
public static class BepInExCompat
{
    public const string PluginFolder = "BepInEx5Compat";
    public static string PluginDir(GameInstall g) => Path.Combine(g.PluginsDir, PluginFolder);
    public static string PluginDll(GameInstall g) => Path.Combine(PluginDir(g), "BepInEx5Compat.dll");
    public static string RuntimeDir(GameInstall g) => Path.Combine(g.BepInExDir, "compat", "BepInEx5");

    public static bool IsInstalled(GameInstall g) => File.Exists(PluginDll(g)) && File.Exists(Path.Combine(RuntimeDir(g), "BepInEx.dll"));

    public static bool IsCompatPlugin(GameInstall g, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(PluginDir(g)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The version this app ships, from the zip's version.txt.</summary>
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
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(PluginDll(g)).FileVersion;
            return Version.TryParse(v, out var ver) ? ver : null;
        }
        catch { return null; }
    }

    public static bool IsOutdated(GameInstall g) => IsInstalled(g) && Bundled() is { } b && Installed(g) is { } i && i < b;

    /// <summary>Unpacks the compatibility layer into the game. Returns the files written (relative to the game folder).</summary>
    public static List<string> Install(GameInstall game)
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

    public static void Remove(GameInstall game)
    {
        try { if (Directory.Exists(PluginDir(game))) Directory.Delete(PluginDir(game), true); } catch { }
        try { if (Directory.Exists(RuntimeDir(game))) Directory.Delete(RuntimeDir(game), true); } catch { }
    }

    private static ZipArchive Open() =>
        new(typeof(BepInExCompat).Assembly.GetManifestResourceStream("Assets.BepInEx5Compat.zip")
            ?? throw new InvalidOperationException("The app is missing its BepInEx 5 compatibility files."), ZipArchiveMode.Read);
}
