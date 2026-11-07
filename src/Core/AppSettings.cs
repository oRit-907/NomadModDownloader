using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NomadModDownloader.Core;

/// <summary>Where the app keeps its own files (never inside the game folder).</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NomadModDownloader");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Downloads => Ensure(Path.Combine(Root, "downloads"));
    public static string Backups => Ensure(Path.Combine(Root, "backups"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ManifestFile => Path.Combine(Root, "installed.json");
    public static string LogFile => Path.Combine(Root, "log.txt");

    public static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

public sealed class AppSettings
{
    public string? GamePath { get; set; }

    /// <summary>Nexus personal API key, encrypted with Windows DPAPI for the current Windows user.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>The nxm:// command that was registered before ours (e.g. Vortex / MO2), so other games' links still go there.</summary>
    public string? PreviousNxmCommand { get; set; }
    public bool PreviousNxmExisted { get; set; }

    public bool ConfirmLinkInstalls { get; set; } = true;
    public bool KeepDownloads { get; set; } = true;

    /// <summary>Free accounts: open Nexus inside the app (true) or in the system browser (false).</summary>
    public bool UseBuiltInBrowser { get; set; } = true;

    /// <summary>Install new versions of this app on startup without asking.</summary>
    public bool AutoUpdateApp { get; set; } = true;

    /// <summary>Keep the game's BepInEx 5 on the latest version (only while the game is closed).</summary>
    public bool AutoUpdateBepInEx { get; set; } = true;

        /// <summary>App language code (en, es, de…). Empty = follow Windows.</summary>
    public string? Language { get; set; }

    /// <summary>Translate Nexus mod summaries into the app language.</summary>
    public bool TranslateNexus { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));

    /// <summary>GitHub token for app updates (the releases repo is private), encrypted for this Windows user.</summary>
    public string? ProtectedGitHubToken { get; set; }
    /// <summary>"Don't ask again" for the update token.</summary>
    public bool SkipUpdateToken { get; set; }

    /// <summary>Back up the game's saves before installing, updating or switching mods.</summary>
    public bool BackupSavesBeforeChanges { get; set; } = true;

    /// <summary>Nomad Drive build seen last time (see GameVersion), to notice game updates.</summary>
    public string? LastGameFingerprint { get; set; }

    /// <summary>Check installed mods for updates when the app starts.</summary>
    public bool CheckModUpdatesOnStart { get; set; } = true;

    /// <summary>Drive the app with an Xbox-style controller (only while the app is the window in front).</summary>
    public bool ControllerSupport { get; set; } = true;

    public string? GetGitHubToken() => Unprotect(ProtectedGitHubToken);
    public void SetGitHubToken(string? token) => ProtectedGitHubToken = string.IsNullOrWhiteSpace(token) ? null : Protect(token.Trim());

    private static string? Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(ProtectedApiKey)) return null;
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public void SetApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !OperatingSystem.IsWindows()) { ProtectedApiKey = null; return; }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        ProtectedApiKey = Convert.ToBase64String(bytes);
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NomadModDownloader.v1");
}
