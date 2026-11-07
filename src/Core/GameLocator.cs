using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NomadModDownloader.Core;

public sealed class GameInstall
{
    public string Root { get; }
    public string ExeName { get; }          // e.g. "Nomad Drive" (no .exe)
    public string DataFolder => Path.Combine(Root, ExeName + "_Data");
    public bool IsIl2Cpp => File.Exists(Path.Combine(Root, "GameAssembly.dll"));
    public bool Is64Bit { get; }

    public string BepInExDir => Path.Combine(Root, "BepInEx");
    public string PluginsDir => Path.Combine(BepInExDir, "plugins");

    /// <summary>The in-game Mods menu (Nomad Mod Manager) is installed: it applies queued changes at launch, so mods can be installed while the game runs.</summary>
    public bool HasModManager => File.Exists(Path.Combine(BepInExDir, "patchers", "NomadModManager.Patcher.dll"))
                                 && Directory.EnumerateFiles(PluginsDir, "NomadModManager.dll", SearchOption.AllDirectories).Any();

    /// <summary>Which BepInEx the installed in-game Mods menu was built for (5 or 6), or null if it isn't installed.</summary>
    public int? ModManagerMajor
    {
        get
        {
            try
            {
                var dll = Directory.EnumerateFiles(PluginsDir, "NomadModManager.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (dll == null) return null;
                using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
                var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
                return refs.Contains("BepInEx.Core") ? 6 : refs.Contains("BepInEx") ? 5 : null;
            }
            catch { return null; }
        }
    }

    /// <summary>The in-game Mods menu file on Nexus for a BepInEx version: its name says "BepInEx 6" (or 5); older files are BepInEx 5.</summary>
    public static int MenuFileMajor(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName ?? "", @"BepInEx\s*6|\bBE\s*6\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 6 : 5;

    /// <summary>Running, and without the in-game Mods menu to take over: installs have to wait.</summary>
    public bool BlocksInstall() => IsRunning() && !SafeHasModManager();

    private bool SafeHasModManager() { try { return HasModManager; } catch { return false; } }

    /// <summary>Tells the in-game Mods menu where this app is, so its Install buttons can open it.</summary>
    public void RegisterWithModManager()
    {
        try
        {
            if (!Directory.Exists(BepInExDir)) return;
            var dir = Path.Combine(BepInExDir, "NomadModManager");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "downloader.txt");
            var exe = StoreApp.LaunchPath;
            // The Store build is started through an alias the Mods menu can't read a version from, so say it here.
            var ver = Path.Combine(dir, "downloader-version.txt");
            var v = Updater.Current.ToString(3);
            if (StoreApp.IsPackaged) { if (!File.Exists(ver) || File.ReadAllText(ver).Trim() != v) File.WriteAllText(ver, v); }
            else if (File.Exists(ver)) File.Delete(ver);
            if (exe.Length == 0 || (File.Exists(file) && File.ReadAllText(file).Trim() == exe)) return;
            File.WriteAllText(file, exe);
        }
        catch { }
    }

    private GameInstall(string root, string exe, bool x64) { Root = root; ExeName = exe; Is64Bit = x64; }

    /// <summary>Returns a GameInstall if the folder looks like a Unity game folder.</summary>
    public static GameInstall? TryOpen(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        foreach (var exe in Directory.EnumerateFiles(folder, "*.exe"))
        {
            var stem = Path.GetFileNameWithoutExtension(exe);
            if (stem.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(folder, stem + "_Data")))
                return new GameInstall(Path.GetFullPath(folder), stem, PeIs64(Path.Combine(folder, "UnityPlayer.dll")) ?? PeIs64(exe) ?? true);
        }
        return null;
    }

    public bool LooksLikeNomadDrive =>
        ExeName.Contains("Nomad", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(Root).Contains("Nomad", StringComparison.OrdinalIgnoreCase);

    public bool IsRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName(ExeName).Length > 0; }
        catch { return false; }
    }

    /// <summary>Reads the PE header machine field: true = x64, false = x86, null = unknown.</summary>
    private static bool? PeIs64(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            fs.Position = peOffset + 4;
            var machine = br.ReadUInt16();
            return machine switch { 0x8664 => true, 0xAA64 => true, 0x014C => false, _ => null };
        }
        catch { return null; }
    }
}

public static class GameLocator
{
    public const string FolderName = "Nomad Drive";
    public const string SteamAppId = "2223980";

    /// <summary>
    /// Quick checks, in order: Steam (by app id, then folder name), the running game,
    /// Windows' installed-programs list, Epic, GOG. Takes well under a second.
    /// </summary>
    public static GameInstall? Find()
    {
        foreach (var dir in QuickCandidates())
        {
            var g = Open(dir);
            if (g != null) return g;
        }
        return null;
    }

    /// <summary>Slower fallback: searches the top few folder levels of every fixed drive.</summary>
    public static GameInstall? DeepSearch(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        foreach (var drive in SafeDrives())
        {
            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((drive, 0));
            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return null;
                var (dir, depth) = queue.Dequeue();
                if (File.Exists(Path.Combine(dir, FolderName + ".exe")))
                {
                    var g = Open(dir);
                    if (g != null) return g;
                }
                if (depth >= 5) continue;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch { continue; }
                foreach (var sub in subs)
                {
                    var name = Path.GetFileName(sub);
                    if (Skip(name, depth)) continue;
                    try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
        }
        return null;
    }

    private static readonly string[] SkipNames =
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "PerfLogs",
        "AppData", "node_modules", ".git", "WindowsApps", "Microsoft", "Windows Kits", "Common Files",
        "WinSxS", "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Config.Msi", "$WINDOWS.~BT", "MSOCache",
        "shadercache", "compatdata", "downloading", "temp", "workshop", "_Data", "BepInEx",
    };

    private static bool Skip(string name, int depth) =>
        name.StartsWith('.') || name.StartsWith('$') || name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ||
        SkipNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
        (depth >= 1 && name.Equals("Users", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens a folder the user picked, forgiving a pick that's one level off:
    /// a folder inside the game (Nomad Drive_Data, BepInEx…) or the folder that contains it.
    /// </summary>
    public static GameInstall? OpenNear(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var g = GameInstall.TryOpen(folder);
        if (g != null) return g;
        var up = new DirectoryInfo(folder).Parent;
        for (var i = 0; i < 3 && up != null; i++, up = up.Parent)
            if ((g = GameInstall.TryOpen(up.FullName)) != null) return g;
        try
        {
            var nomad = Path.Combine(folder, FolderName);
            if ((g = GameInstall.TryOpen(nomad)) != null) return g;
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if ((g = GameInstall.TryOpen(sub)) != null && g.LooksLikeNomadDrive) return g;
        }
        catch { }
        return null;
    }

    private static GameInstall? Open(string? dir)
    {
        var g = GameInstall.TryOpen(dir);
        return g != null && g.LooksLikeNomadDrive ? g : null;
    }

    private static IEnumerable<string> QuickCandidates()
    {
        var libs = SteamLibraries().ToList();

        // 1. Steam's own record for the game (works even if the install folder was renamed).
        foreach (var lib in libs)
        {
            var dir = FromAppManifest(lib);
            if (dir != null) yield return dir;
        }
        // 2. The usual Steam folder.
        foreach (var lib in libs) yield return Path.Combine(lib, "steamapps", "common", FolderName);

        // 3. The game, if it's running right now.
        foreach (var dir in RunningGame()) yield return dir;

        // 4. Windows' installed-programs list (Steam, GOG, itch and other installers write here).
        foreach (var dir in UninstallEntries()) yield return dir;

        // 5. Epic and GOG launchers.
        foreach (var dir in EpicInstalls()) yield return dir;
        foreach (var dir in GogInstalls()) yield return dir;

        // 6. Common non-Steam spots.
        foreach (var d in SafeDrives())
            foreach (var sub in new[] { "", "Games", @"Program Files", @"Program Files (x86)", @"Program Files\Epic Games", @"Program Files (x86)\GOG Galaxy\Games", "GOG Games", "Epic Games", "Games\\Epic Games", "Games\\GOG" })
                yield return Path.Combine(d, sub, FolderName);
    }

    private static string? FromAppManifest(string lib)
    {
        try
        {
            var steamapps = Path.Combine(lib, "steamapps");
            var acf = Path.Combine(steamapps, "appmanifest_" + SteamAppId + ".acf");
            string? text = File.Exists(acf) ? File.ReadAllText(acf) : null;
            if (text == null)
            {
                foreach (var f in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    var t = File.ReadAllText(f);
                    if (Regex.IsMatch(t, "\"name\"\\s+\"Nomad Drive\"", RegexOptions.IgnoreCase)) { text = t; break; }
                }
            }
            if (text == null) return null;
            var m = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
            return m.Success ? Path.Combine(steamapps, "common", m.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    private static IEnumerable<string> RunningGame()
    {
        var list = new List<string>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(FolderName))
            {
                try { var exe = p.MainModule?.FileName; if (exe != null) list.Add(Path.GetDirectoryName(exe)!); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return list;
    }

    private static IEnumerable<string> UninstallEntries()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(key);
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var k = uninstall.OpenSubKey(name);
                        var display = k?.GetValue("DisplayName") as string;
                        if (name != "Steam App " + SteamAppId && (display == null || !display.Contains(FolderName, StringComparison.OrdinalIgnoreCase))) continue;
                        if (k?.GetValue("InstallLocation") is string loc && loc.Length > 0) list.Add(loc.Trim('"'));
                        if (k?.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        {
                            var path = icon.Split(',')[0].Trim('"');
                            try { if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) list.Add(Path.GetDirectoryName(path)!); } catch { }
                        }
                    }
                }
                catch { }
            }
        return list;
    }

    private static IEnumerable<string> EpicInstalls()
    {
        var list = new List<string>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    var name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                    if (name == null || !name.Contains(FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (r.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } s) list.Add(s);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static IEnumerable<string> GogInstalls()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var games = root.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games == null) return list;
            foreach (var id in games.GetSubKeyNames())
            {
                using var k = games.OpenSubKey(id);
                if (k?.GetValue("gameName") is string n && n.Contains(FolderName, StringComparison.OrdinalIgnoreCase) && k.GetValue("path") is string p)
                    list.Add(p);
            }
        }
        catch { }
        return list;
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            void Add(object? v) { if (v is string s && !string.IsNullOrWhiteSpace(s)) roots.Add(s.Replace('/', '\\')); }
            try { Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null)); } catch { }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libs = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            libs.Add(root);
            foreach (var vdf in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
            {
                if (!File.Exists(vdf)) continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
        }

        // Also try every drive's common Steam folder names.
        foreach (var d in SafeDrives())
            foreach (var sub in new[] { "SteamLibrary", "Steam", @"Program Files\Steam", @"Program Files (x86)\Steam", "Games\\Steam", "Games\\SteamLibrary", "Steam Library", "Steam Games" })
                libs.Add(Path.Combine(d, sub));

        return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Steam's cached library art for the game (logo.png, library_hero.jpg…), if Steam has it.</summary>
    public static string? SteamLibraryImage(string fileName)
    {
        foreach (var lib in SteamLibraries())
        {
            var dir = Path.Combine(lib, "appcache", "librarycache");
            if (!Directory.Exists(dir)) continue;
            try
            {
                var flat = Path.Combine(dir, SteamAppId + "_" + fileName);
                if (File.Exists(flat)) return flat;
                var sub = Path.Combine(dir, SteamAppId);
                if (Directory.Exists(sub))
                    foreach (var f in Directory.EnumerateFiles(sub, fileName, SearchOption.AllDirectories)) return f;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> SafeDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
