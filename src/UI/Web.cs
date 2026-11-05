using System.Diagnostics;

namespace NomadModDownloader.UI;

internal static class Web
{
    /// <summary>Opens a link in your normal web browser.</summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
