using System.IO.Compression;

namespace NomadModDownloader.Core;

/// <summary>
/// "BepInEx 6 Compatibility": a BepInEx 5 plugin (mod/BepInEx6Compat) that runs BepInEx 6's own runtime beside
/// BepInEx 5, so mods built for BepInEx 6 load on BepInEx 5 too. The mirror image of <see cref="BepInExCompat"/>.
/// Shipped inside the app (Assets/BepInEx6Compat.zip) and set up with every BepInEx 5 install.
/// </summary>
public static class BepInEx6Support
{
    public const string PluginFolder = "BepInEx6Compat";
    public static string PluginDir(GameInstall g) => Path.Combine(g.PluginsDir, PluginFolder);
    public static string PluginDll(GameInstall g) => Path.Combine(PluginDir(g), "BepInEx6Compat.dll");
    public static string RuntimeDir(GameInstall g) => Path.Combine(g.BepInExDir, "compat", "BepInEx6");

    public static bool IsInstalled(GameInstall g) => File.Exists(PluginDll(g)) && File.Exists(Path.Combine(RuntimeDir(g), "BepInEx.Core.dll"));

    public static bool IsCompatPlugin(GameInstall g, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(PluginDir(g)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Either compatibility layer's plugin folder (part of BepInEx, not a mod).</summary>
    public static bool IsAnyCompatPlugin(GameInstall g, string path) => IsCompatPlugin(g, path) || BepInExCompat.IsCompatPlugin(g, path);
    public static bool IsAnyCompatFolder(string rel) =>
        rel.EndsWith("/" + PluginFolder, StringComparison.OrdinalIgnoreCase) || rel.EndsWith("/" + BepInExCompat.PluginFolder, StringComparison.OrdinalIgnoreCase);

    private static ZipArchive Open() =>
        new(typeof(BepInEx6Support).Assembly.GetManifestResourceStream("Assets.BepInEx6Compat.zip")
            ?? throw new InvalidOperationException("The app is missing its BepInEx 6 compatibility files."), ZipArchiveMode.Read);
}
