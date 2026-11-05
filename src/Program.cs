using System.IO.Pipes;
using NomadModDownloader.Core;

namespace NomadModDownloader;

internal static class Program
{
    private const string MutexName = @"Local\NomadModDownloader.SingleInstance";
    public const string PipeName = "NomadModDownloader.Links";

    [STAThread]
    private static void Main(string[] args)
    {
        L.Init(AppSettings.Load().Language);
        var link = NxmLink.FindInArgs(args);

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
        Updater.CleanUpAfterUpdate(); // remove the previous version's exe after an update

        // The update screen: checks for a new version first, and restarts into it if there is one.
        var appChecked = false;
        using (var splash = new NomadModDownloader.UI.UpdateScreen())
        {
            Application.Run(splash);
            if (splash.Restarting) return;
            appChecked = splash.Checked;
        }
        Application.Run(new MainForm(link, appChecked));
    }
}
