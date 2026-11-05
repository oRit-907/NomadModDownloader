using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

/// <summary>Starts Nomad Drive (through Steam when it's a Steam copy), optionally with mods off for that one run.</summary>
public static class GameLauncher
{
    private static string PendingFile => Path.Combine(AppPaths.Root, "vanilla-run.txt");

    public static bool IsSteamCopy(GameInstall game) =>
        game.Root.Replace('/', '\\').Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);

    public static void Launch(GameInstall game)
    {
        if (IsSteamCopy(game))
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + GameLocator.SteamAppId) { UseShellExecute = true });
            return;
        }
        var exe = Path.Combine(game.Root, game.ExeName + ".exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = game.Root });
    }

    /// <summary>
    /// Switches BepInEx off in doorstop_config.ini, starts the game, and switches it back on once the game
    /// has started (BepInEx only reads the setting at startup). If the app closes first, the next start puts it back.
    /// </summary>
    public static async Task LaunchVanillaAsync(GameInstall game, CancellationToken ct)
    {
        var ini = Path.Combine(game.Root, "doorstop_config.ini");
        if (!File.Exists(ini)) { Launch(game); return; }
        if (!SetEnabled(ini, false)) { Launch(game); return; } // already off: nothing to put back
        File.WriteAllText(PendingFile, game.Root);
        try
        {
            Launch(game);
            // Wait for the game to start (Steam can take a while), then give doorstop time to read its config.
            var until = DateTime.UtcNow.AddMinutes(3);
            while (!game.IsRunning() && DateTime.UtcNow < until) await Task.Delay(1000, ct);
            if (game.IsRunning()) await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        finally { RestoreIfPending(); }
    }

    /// <summary>Turns BepInEx back on after a "play without mods" run the app didn't finish (call at startup).</summary>
    public static bool RestoreIfPending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return false;
            var root = File.ReadAllText(PendingFile).Trim();
            var ini = Path.Combine(root, "doorstop_config.ini");
            if (File.Exists(ini)) SetEnabled(ini, true);
            File.Delete(PendingFile);
            return true;
        }
        catch { return false; }
    }

    /// <returns>true if the file was changed.</returns>
    private static bool SetEnabled(string ini, bool on)
    {
        var text = File.ReadAllText(ini);
        var from = on ? "false" : "true";
        var re = new Regex(@"(?im)^(\s*enabled\s*=\s*)" + from + @"\b");
        if (!re.IsMatch(text)) return false;
        File.WriteAllText(ini, re.Replace(text, "${1}" + (on ? "true" : "false"), 1));
        return true;
    }
}

/// <summary>Notices when Nomad Drive itself has been updated (Steam build id, or the game's code DLL changing).</summary>
public static class GameVersion
{
    public static string Fingerprint(GameInstall game)
    {
        string build = "";
        try
        {
            // ...\steamapps\common\Nomad Drive → ...\steamapps\appmanifest_2223980.acf
            var steamapps = Directory.GetParent(game.Root)?.Parent?.FullName;
            var acf = steamapps == null ? null : Path.Combine(steamapps, "appmanifest_" + GameLocator.SteamAppId + ".acf");
            if (acf != null && File.Exists(acf))
            {
                var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d+)\"");
                if (m.Success) build = "steam:" + m.Groups[1].Value;
            }
        }
        catch { }
        if (build.Length > 0) return build;
        try
        {
            var dll = Path.Combine(game.DataFolder, "Managed", "Assembly-CSharp.dll");
            if (File.Exists(dll)) { var fi = new FileInfo(dll); return $"dll:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"; }
        }
        catch { }
        return "";
    }
}
