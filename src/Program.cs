using System.IO.Pipes;
using NomadModDownloader.Core;

namespace NomadModDownloader;

/// <summary>Requests from the in-game Mods menu (Nomad Mod Manager), passed like Nexus links.</summary>
internal static class GameLink
{
    public const string Prefix = "nomadmods://install/";
    public const string CheckLink = "nomadmods://check";

    /// <summary>"12" (latest), "12:345" (a specific file) or "12,34,56" (several, one after the other).</summary>
    public static bool TryParse(string? line, out List<(int ModId, int? FileId)> items)
    {
        items = new();
        if (line == null || !line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in line[Prefix.Length..].Trim('/', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (!int.TryParse(bits[0], out var mod) || mod <= 0) continue;
            int? file = bits.Length > 1 && int.TryParse(bits[1], out var f) && f > 0 ? f : null;
            if (!items.Any(i => i.ModId == mod)) items.Add((mod, file));
        }
        return items.Count > 0;
    }
}

internal static class Program
{
    private const string MutexName = @"Local\NomadModDownloader.SingleInstance";
    public const string PipeName = "NomadModDownloader.Links";

    [STAThread]
    private static void Main(string[] args)
    {
        L.Init(AppSettings.Load().Language);
        var link = NxmLink.FindInArgs(args);
        // From the in-game Mods menu: "--game-install <modId>"
        var gi = Array.FindIndex(args, a => a.Equals("--game-install", StringComparison.OrdinalIgnoreCase));
        if (link == null && gi >= 0 && gi + 1 < args.Length && GameLink.TryParse(GameLink.Prefix + args[gi + 1], out _))
            link = GameLink.Prefix + args[gi + 1];
        // "--game-check": open Check BepInEx
        if (link == null && args.Any(a => a.Equals("--game-check", StringComparison.OrdinalIgnoreCase))) link = GameLink.CheckLink;

        // Links for other games go straight back to the previous mod manager (Vortex/MO2) without opening this app.
        if (link != null && NxmLink.TryParse(link, out var parsed) && parsed is { IsNomadDrive: false })
        {
            var settings = AppSettings.Load();
            if (!ProtocolHandler.ForwardToPrevious(settings, link))
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show(L.F("This link is for \"{0}\", not Nomad Drive.\n\nNomad Mod Downloader only installs Nomad Drive mods, and no other mod manager was registered before it to pass the link to.", parsed.GameDomain),
                    L.T("Nomad Mod Downloader"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: hand the link (or just a "show yourself" ping) to that window.
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var w = new StreamWriter(client) { AutoFlush = true };
                w.WriteLine(link ?? "show");
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        NomadModDownloader.UI.Theme.Init();
        // The Microsoft Store build is updated by the Store, not by the app.
        var appChecked = StoreApp.IsPackaged;
        if (!StoreApp.IsPackaged)
        {
            Updater.CleanUpAfterUpdate(); // remove the previous version's exe after an update

            // The update screen: checks for a new version first, and restarts into it if there is one.
            using var splash = new NomadModDownloader.UI.UpdateScreen();
            Application.Run(splash);
            if (splash.Restarting) return;
            appChecked = splash.Checked;
        }
        Application.Run(new MainForm(link, appChecked));
    }
}
