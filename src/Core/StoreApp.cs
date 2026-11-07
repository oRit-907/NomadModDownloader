using System.Runtime.InteropServices;

namespace NomadModDownloader.Core;

/// <summary>
/// The Microsoft Store (MSIX) build. The Store installs and updates it, so the app doesn't update itself,
/// and other programs (the in-game Mods menu) start it through its app execution alias, not the exe inside WindowsApps.
/// </summary>
public static class StoreApp
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    private static bool? _packaged;

    /// <summary>Running as the installed Store package.</summary>
    public static bool IsPackaged
    {
        get
        {
            if (_packaged is bool b) return b;
            try
            {
                var len = 0;
                // APPMODEL_ERROR_NO_PACKAGE (15700) when it's the plain exe; ERROR_INSUFFICIENT_BUFFER (122) when packaged.
                _packaged = GetCurrentPackageFullName(ref len, null) != 15700;
            }
            catch { _packaged = false; }
            return _packaged.Value;
        }
    }

    /// <summary>The name of the app execution alias in Package.appxmanifest.</summary>
    public const string AliasName = "NomadModDownloader.exe";

    /// <summary>The path other programs should start: the alias for the Store build, the exe itself otherwise.</summary>
    public static string LaunchPath
    {
        get
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (!IsPackaged) return exe;
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", AliasName);
            return File.Exists(alias) ? alias : exe;
        }
    }
}
