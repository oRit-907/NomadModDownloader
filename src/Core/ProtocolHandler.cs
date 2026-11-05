using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NomadModDownloader.Core;

/// <summary>
/// Registers this app for nxm:// links (current Windows user only, no admin needed).
/// Whatever handled nxm:// before (Vortex, MO2...) is remembered, and links for
/// other games are passed straight back to it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtocolHandler
{
    private const string KeyPath = @"Software\Classes\nxm";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string OurCommand => $"\"{ExePath}\" \"%1\"";

    public static string? CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
        var cmd = k?.GetValue(null) as string;
        if (cmd != null) return cmd;
        // Machine-wide registration (rare)
        using var m = Registry.ClassesRoot.OpenSubKey(@"nxm\shell\open\command");
        return m?.GetValue(null) as string;
    }

    public static bool IsOurs()
    {
        var cmd = CurrentCommand();
        return cmd != null && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register(AppSettings settings)
    {
        var existing = CurrentCommand();
        if (existing != null && !existing.Contains(ExePath, StringComparison.OrdinalIgnoreCase)
            && !existing.Contains("NomadModDownloader", StringComparison.OrdinalIgnoreCase))
        {
            settings.PreviousNxmCommand = existing;
            settings.PreviousNxmExisted = true;
        }
        else if (existing == null)
        {
            settings.PreviousNxmCommand = null;
            settings.PreviousNxmExisted = false;
        }
        settings.Save();

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:NXM Protocol");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{ExePath}\",0");
        using var cmdKey = key.CreateSubKey(@"shell\open\command");
        cmdKey.SetValue(null, OurCommand);
    }

    /// <summary>Gives nxm:// back to whatever had it before.</summary>
    public static void Unregister(AppSettings settings)
    {
        if (!IsOurs()) return;
        if (settings.PreviousNxmExisted && !string.IsNullOrEmpty(settings.PreviousNxmCommand))
        {
            using var cmdKey = Registry.CurrentUser.CreateSubKey(KeyPath + @"\shell\open\command");
            cmdKey.SetValue(null, settings.PreviousNxmCommand);
            try
            {
                var exe = SplitCommand(settings.PreviousNxmCommand).Exe;
                using var icon = Registry.CurrentUser.CreateSubKey(KeyPath + @"\DefaultIcon");
                icon.SetValue(null, $"\"{exe}\",0");
            }
            catch { }
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        }
    }

    /// <summary>Hands a link for another game to the previously registered mod manager.</summary>
    public static bool ForwardToPrevious(AppSettings settings, string url)
    {
        var cmd = settings.PreviousNxmCommand;
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        try
        {
            var (exe, args) = SplitCommand(cmd);
            args = args.Contains("%1") ? args.Replace("%1", url) : $"{args} \"{url}\"";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    private static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }
}
