using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

public enum CheckLevel { Ok, Info, Warning, Error }

/// <summary>What the "Fix" button does for a check. Every fix is shown to the user and confirmed first.</summary>
public enum CheckFix
{
    None,
    InstallBepInEx,     // download and install BepInEx 5
    RepairBepInEx,      // reinstall BepInEx 5's core files (plugins and settings kept)
    UpdateBepInEx,
    UpgradeToBepInEx6,  // BepInEx 5 is installed: move to BepInEx 6 + BepInEx 5 support (plugins and settings kept)
    InstallCompat,      // BepInEx 6 without the BepInEx 5 compatibility layer (or an older copy of it)
    InstallCompat6,     // BepInEx 5 without the BepInEx 6 compatibility layer (or an older copy of it)
    WriteDoorstop,      // write a working doorstop_config.ini
    EnableDoorstop,
    InstallDotNetFix,   // Nomad Drive's full .NET libraries + point doorstop at them
    OpenModsMenuPage,   // show the in-game Mods menu's files (on this app's Nexus page) in Browse
    InstallUnityCompat, // restore the Unity code the game's build stripped (downloads Unity's libraries; UI flow)
    EnableDiskLog,
    Unblock,            // remove the "downloaded from the internet" mark
    RemoveMelonLoader,  // moved to a backup, not deleted
    RemoveOtherLoader,
    RemoveEmptyFiles,
    FlattenNested,      // plugins/Mod/BepInEx/plugins/* → plugins/Mod/*
    ClearOldCrashLogs,
    OpenProblems,       // UI only
    OpenInstalled,      // UI only
    Play,               // UI only
}

public sealed class CheckItem
{
    public string Area { get; init; } = "";
    public CheckLevel Level { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public CheckFix Fix { get; init; }
    public List<string> Paths { get; init; } = new();

    /// <summary>Fixes that change files and can be run together by "Fix all".</summary>
    public bool FileFix => Fix is not (CheckFix.None or CheckFix.OpenProblems or CheckFix.OpenInstalled or CheckFix.Play);
}

/// <summary>
/// Checks a Nomad Drive install for everything that stops BepInEx and mods from loading, and fixes it.
///
/// Nomad Drive specifics: the game ships a code-stripped mscorlib (missing e.g. Module.GetPEKind), which crashes
/// BepInEx's preloader. BepInEx needs Mono's full class libraries in a folder that doorstop puts first on the
/// search path (dll_search_path_override). The app ships those (Mono, MIT) as DotNetFix.
/// </summary>
public static class BepInExChecker
{
    public const string AreaInstall = "BepInEx install";
    public const string AreaNomad = "Nomad Drive setup";
    public const string AreaMods = "Mods";
    public const string AreaBreakages = "Common problems";
    public static readonly string[] Areas = { AreaInstall, AreaNomad, AreaMods, AreaBreakages };

    public const string DotNetFixFolder = "DotNetFix";

    private static readonly string[] CoreFiles5 =
        { "BepInEx.dll", "BepInEx.Preloader.dll", "0Harmony.dll", "HarmonyXInterop.dll", "Mono.Cecil.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll" };
    private static readonly string[] CoreFiles6 =
        { "BepInEx.Core.dll", "BepInEx.Preloader.Core.dll", "BepInEx.Unity.Mono.dll", "BepInEx.Unity.Mono.Preloader.dll", "BepInEx.Unity.Common.dll", "0Harmony.dll", "Mono.Cecil.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "SemanticVersioning.dll" };
    /// <summary>BepInEx builds for IL2CPP games, which can't run on Nomad Drive (a Mono game).</summary>
    private static readonly string[] WrongBuildMarkers = { "BepInEx.Unity.IL2CPP.dll", "BepInEx.IL2CPP.dll", "Il2CppInterop.Runtime.dll" };

    public static string PreloaderFor(int major) => major == 6 ? @"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll" : @"BepInEx\core\BepInEx.Preloader.dll";
    private static readonly string[] MelonLoaderBits = { "MelonLoader", "Mods", "Plugins", "UserData", "UserLibs" };
    private static readonly string[] OtherProxies = { "version.dll", "winmm.dll", "dinput8.dll" };

    public static string DoorstopIni(GameInstall g) => Path.Combine(g.Root, "doorstop_config.ini");
    private static string Cfg(GameInstall g) => Path.Combine(g.BepInExDir, "config", "BepInEx.cfg");
    private static string GameCorlib(GameInstall g) => Path.Combine(g.DataFolder, "Managed", "mscorlib.dll");

    // ==================================================================
    // Checks

    public static List<CheckItem> Run(GameInstall game, Manifest manifest, string? latestBepInEx)
    {
        var list = new List<CheckItem>();
        void Add(string area, CheckLevel level, string title, string detail = "", CheckFix fix = CheckFix.None, IEnumerable<string>? paths = null)
            => list.Add(new CheckItem { Area = area, Level = level, Title = title, Detail = detail, Fix = fix, Paths = paths?.ToList() ?? new() });

        var status = BepInExManager.Check(game);

        // ---------------- BepInEx install ----------------
        if (game.IsIl2Cpp)
        {
            Add(AreaInstall, CheckLevel.Error, L.T("This copy of Nomad Drive is an IL2CPP build."),
                L.T("IL2CPP games need BepInEx 6, which this app can't set up. Nomad Drive normally isn't IL2CPP: try Steam's \"Verify integrity of game files\"."));
            return list;
        }

        var core = Path.Combine(game.BepInExDir, "core");
        var major = status.Major;
        var coreFiles = major == 6 ? CoreFiles6 : CoreFiles5;
        var hasCore = File.Exists(Path.Combine(core, coreFiles[0]));
        var proxy = Path.Combine(game.Root, "winhttp.dll");
        if (status.State == BepInExState.NotInstalled)
        {
            Add(AreaInstall, CheckLevel.Error, L.T("BepInEx isn't installed."), L.T("Nomad Drive mods need BepInEx. The fix lets you pick BepInEx 6 (recommended) or BepInEx 5, downloads it from BepInEx's GitHub and sets it up for Nomad Drive, with support for the other version's mods."), CheckFix.InstallBepInEx);
        }
        else
        {
            var wrong = WrongBuildMarkers.Where(f => File.Exists(Path.Combine(core, f))).ToList();
            if (wrong.Count > 0)
                Add(AreaInstall, CheckLevel.Error, L.T("A BepInEx made for IL2CPP games is installed."),
                    L.T("Nomad Drive is a Mono game, so IL2CPP builds can't run on it. The fix installs BepInEx 6 for Mono. Found: ") + string.Join(", ", wrong), CheckFix.RepairBepInEx);
            else if (major == 6)
            {
                Add(AreaInstall, CheckLevel.Ok, L.F("BepInEx {0} is installed.", status.Version ?? "6"));
                var built5 = ModLoadDoctor.ModsBuiltFor(game, "5");
                if (!BepInExCompat.IsInstalled(game))
                    Add(AreaInstall, built5.Count > 0 ? CheckLevel.Error : CheckLevel.Warning, L.T("BepInEx 5 support isn't installed."),
                        (built5.Count > 0 ? L.F("{0} of your mods are built for BepInEx 5 and can't load without it:\n", built5.Count) + string.Join("\n", built5.Take(12).Select(m => "• " + m)) + "\n\n" : "") +
                        L.T("BepInEx 6 can't load BepInEx 5 mods by itself. The BepInEx 5 compatibility layer runs them beside BepInEx 6, and most Nomad Drive mods are still built for BepInEx 5."),
                        CheckFix.InstallCompat);
                else if (BepInExCompat.IsOutdated(game))
                    Add(AreaInstall, CheckLevel.Info, L.T("A newer BepInEx 5 support layer is available."), L.F("You have {0}; this app has {1}.", BepInExCompat.Installed(game), BepInExCompat.Bundled()), CheckFix.InstallCompat);
                else
                    Add(AreaInstall, CheckLevel.Ok, built5.Count > 0 ? L.F("BepInEx 5 support is installed ({0} BepInEx 5 mod(s)).", built5.Count) : L.T("BepInEx 5 support is installed."));
            }
            else
            {
                Add(AreaInstall, CheckLevel.Info, L.F("BepInEx {0} is installed. This app now uses BepInEx 6.", status.Version ?? "5"),
                    L.T("Upgrading installs BepInEx 6 with BepInEx 5 support, so your current mods keep working and mods built for BepInEx 6 load too. Your plugins and settings are kept, the old BepInEx goes to a backup, and you can switch back in Check BepInEx."),
                    CheckFix.UpgradeToBepInEx6);
                var built6 = ModLoadDoctor.ModsBuiltFor(game, "6");
                if (!BepInEx6Support.IsInstalled(game))
                    Add(AreaInstall, built6.Count > 0 ? CheckLevel.Error : CheckLevel.Warning, L.T("BepInEx 6 support isn't installed."),
                        (built6.Count > 0 ? L.F("{0} of your mods are built for BepInEx 6 and can't load without it:\n", built6.Count) + string.Join("\n", built6.Take(12).Select(m => "• " + m)) + "\n\n" : "") +
                        L.T("BepInEx 5 can't load BepInEx 6 mods by itself. The BepInEx 6 compatibility layer runs them beside BepInEx 5."),
                        CheckFix.InstallCompat6);
                else if (BepInEx6Support.IsOutdated(game))
                    Add(AreaInstall, CheckLevel.Info, L.T("A newer BepInEx 6 support layer is available."), L.F("You have {0}; this app has {1}.", BepInEx6Support.Installed(game), BepInEx6Support.Bundled()), CheckFix.InstallCompat6);
                else
                    Add(AreaInstall, CheckLevel.Ok, built6.Count > 0 ? L.F("BepInEx 6 support is installed ({0} BepInEx 6 mod(s)).", built6.Count) : L.T("BepInEx 6 support is installed."));
            }

            var missing = coreFiles.Where(f => !File.Exists(Path.Combine(core, f))).ToList();
            var empty = coreFiles.Where(f => File.Exists(Path.Combine(core, f)) && new FileInfo(Path.Combine(core, f)).Length == 0).ToList();
            if (missing.Count > 0 || empty.Count > 0)
                Add(AreaInstall, CheckLevel.Error, L.T("Some of BepInEx's own files are missing or empty."),
                    L.T("Antivirus programs sometimes remove them. Missing: ") + string.Join(", ", missing.Concat(empty)), CheckFix.RepairBepInEx);
            else if (hasCore)
                Add(AreaInstall, CheckLevel.Ok, L.T("BepInEx's core files are all there."));

            if (!File.Exists(proxy))
                Add(AreaInstall, CheckLevel.Error, L.T("winhttp.dll is missing, so BepInEx never starts."),
                    L.T("It's the small file next to Nomad Drive.exe that starts BepInEx. Antivirus programs sometimes remove it."), CheckFix.RepairBepInEx);
            else if (new FileInfo(proxy).Length == 0)
                Add(AreaInstall, CheckLevel.Error, L.T("winhttp.dll is empty (0 bytes)."), L.T("Probably blocked by antivirus."), CheckFix.RepairBepInEx);
            else if (MachineOf(proxy) is { } pm && MachineOf(Path.Combine(game.Root, game.ExeName + ".exe")) is { } gm && pm != gm)
                Add(AreaInstall, CheckLevel.Error, L.T("winhttp.dll is the wrong type (32-bit vs 64-bit)."), L.T("Nomad Drive is 64-bit and needs the x64 BepInEx."), CheckFix.RepairBepInEx);
            else
                Add(AreaInstall, CheckLevel.Ok, L.T("winhttp.dll (the BepInEx starter) is in place."));

            // doorstop_config.ini
            var ini = DoorstopIni(game);
            if (!File.Exists(ini))
                Add(AreaInstall, CheckLevel.Error, L.T("doorstop_config.ini is missing."), L.T("It tells winhttp.dll where BepInEx is."), CheckFix.WriteDoorstop);
            else
            {
                var text = SafeRead(ini);
                var target = IniValue(text, "target_assembly") ?? IniValue(text, "targetAssembly");
                if (Regex.IsMatch(text, @"(?im)^\s*enabled\s*=\s*false"))
                    Add(AreaInstall, CheckLevel.Warning, L.T("BepInEx is switched off (enabled = false in doorstop_config.ini)."), L.T("Mods won't load until it's switched back on."), CheckFix.EnableDoorstop);
                else if (string.IsNullOrWhiteSpace(target) || !File.Exists(Path.Combine(game.Root, NormPath(target))))
                    Add(AreaInstall, CheckLevel.Error, L.T("doorstop_config.ini points at a file that isn't there."), L.F("target_assembly = {0}", target ?? "(empty)"), CheckFix.WriteDoorstop);
                else
                    Add(AreaInstall, CheckLevel.Ok, L.T("doorstop_config.ini is set up and switched on."));
            }

            if (!File.Exists(Cfg(game)))
                Add(AreaInstall, CheckLevel.Info, L.T("BepInEx hasn't finished its first run yet."), L.T("Play the game once to the main menu, then check again."), CheckFix.Play);
            else if (Regex.IsMatch(Section(SafeRead(Cfg(game)), "Logging.Disk"), @"(?im)^\s*Enabled\s*=\s*false"))
                Add(AreaInstall, CheckLevel.Warning, L.T("BepInEx's log file is switched off."), L.T("Without LogOutput.log, nothing can tell which mod failed."), CheckFix.EnableDiskLog);
        }

        // ---------------- Nomad Drive setup ----------------
        var stripped = CorlibIsStripped(GameCorlib(game));
        if (stripped == true)
        {
            var dir = DotNetFixDir(game);
            if (dir == null)
                Add(AreaNomad, CheckLevel.Error, L.T("Nomad Drive's .NET fix isn't set up."),
                    L.T("Nomad Drive ships a cut-down mscorlib.dll that BepInEx can't start with (it crashes with \"Method not found: GetPEKind\"). The fix adds Mono's full .NET libraries in a DotNetFix folder and points doorstop at them."),
                    CheckFix.InstallDotNetFix);
            else if (CorlibIsStripped(Path.Combine(dir, "mscorlib.dll")) != false)
                Add(AreaNomad, CheckLevel.Error, L.F("The .NET fix folder ({0}) doesn't have the full mscorlib.dll.", Path.GetFileName(dir)), "", CheckFix.InstallDotNetFix);
            else
                Add(AreaNomad, CheckLevel.Ok, L.F("Nomad Drive's .NET fix is set up ({0}).", Path.GetFileName(dir)));
        }
        else if (stripped == false)
            Add(AreaNomad, CheckLevel.Ok, L.T("This version of Nomad Drive doesn't need the .NET fix."));

        // .NET Standard 2.1: Nomad Drive ships no netstandard.dll, so mods built for .NET Standard can't load without one.
        var ns = NetStandardAvailable(game);
        var needNs = ModsNeedingNetStandard(game);
        var needs21 = needNs.Where(m => m.Version >= new Version(2, 1)).Select(m => m.Name).ToList();
        if (ns == null || ns < new Version(2, 1))
            Add(AreaNomad, needNs.Count > 0 ? CheckLevel.Error : CheckLevel.Warning,
                ns == null ? L.T(".NET Standard support isn't set up.") : L.F("Only .NET Standard {0} is set up, not 2.1.", ns.ToString(2)),
                (needNs.Count > 0 ? L.F("{0} of your mods are built for .NET Standard and can't load without it:\n", needNs.Count) + string.Join("\n", needNs.Take(12).Select(m => "• " + m.Name + " (.NET Standard " + m.Version.ToString(2) + ")")) + "\n\n" : "") +
                L.T("Nomad Drive doesn't include netstandard.dll, which mods built for .NET Standard 2.0 or 2.1 need. The fix adds Mono's .NET Standard 2.1 library (and the full Mono.Security it uses) to the .NET fix folder."),
                CheckFix.InstallDotNetFix);
        else
            Add(AreaNomad, CheckLevel.Ok, needs21.Count > 0 ? L.F(".NET Standard 2.1 is supported ({0} mod(s) use it).", needs21.Count) : L.T(".NET Standard 2.1 is supported."));

        // The in-game Mods menu has a build per BepInEx version.
        if (status.State != BepInExState.NotInstalled && game.ModManagerMajor is { } menuMajor && menuMajor != major)
        {
            if (menuMajor == 6)
                Add(AreaMods, CheckLevel.Error, L.T("The in-game Mods menu is the BepInEx 6 version, but the game has BepInEx 5."),
                    L.T("It won't load on BepInEx 5. Install the BepInEx 5 version of the Mods menu: Browse > Nomad Mod Downloader > the \"BepInEx 5\" file."), CheckFix.OpenModsMenuPage);
            else
                Add(AreaMods, CheckLevel.Warning, L.T("The in-game Mods menu is the BepInEx 5 version."),
                    L.T("It still runs on BepInEx 6, but changes you make in it while the game is running (updates, removals, switching mods on and off) aren't applied at the next start. Install the BepInEx 6 version: Browse > Nomad Mod Downloader > the \"BepInEx 6\" file."), CheckFix.OpenModsMenuPage);
        }

        // Unity Compatibility: the game's Unity files are stripped, so mods calling Unity code the game never uses fail.
        if (major is 5 or 6 && status.State != BepInExState.NotInstalled)
        {
            List<UnityCompat.ModNeed> needs;
            try { needs = UnityCompat.ScanMods(game); } catch { needs = new(); }
            var fixable = needs.Where(n => n.Restorable.Count > 0).ToList();
            string ListMods(IEnumerable<UnityCompat.ModNeed> mods, Func<UnityCompat.ModNeed, List<GameApiScanner.Missing>> pick) =>
                string.Join("\n", mods.Take(10).Select(n => "• " + n.Mod + ": " + string.Join(", ", pick(n).Take(3).Select(m => m.Display.Replace("UnityEngine.", ""))) + (pick(n).Count > 3 ? L.F(" and {0} more", pick(n).Count - 3) : "")));
            if (!UnityCompat.IsInstalled(game))
                Add(AreaNomad, fixable.Count > 0 ? CheckLevel.Error : CheckLevel.Warning, L.T("Unity Compatibility isn't set up."),
                    (fixable.Count > 0 ? L.F("{0} of your mods use Unity code that Nomad Drive's build left out, so they fail with \"Method not found\":\n", fixable.Count) + ListMods(fixable, n => n.Restorable) + "\n\n" : "") +
                    L.T("Nomad Drive's Unity files only contain the parts the game itself uses. Unity Compatibility puts the rest back when the game starts, from Unity's own libraries for the same Unity version (downloaded from BepInEx's server, about 2.5 MB). It only adds what's missing; the game's own code stays as it is."),
                    CheckFix.InstallUnityCompat);
            else if (UnityCompat.IsOutdated(game))
                Add(AreaNomad, CheckLevel.Info, L.T("A newer Unity Compatibility is available."), L.F("You have {0}; this app has {1}.", UnityCompat.Installed(game), UnityCompat.Bundled()), CheckFix.InstallUnityCompat);
            else
                Add(AreaNomad, CheckLevel.Ok, (UnityCompat.IsBuilt(game) ? L.T("Unity Compatibility is set up.") : L.T("Unity Compatibility is set up. It finishes on the next game start."))
                    + (fixable.Count > 0 ? " " + L.F("({0} mod(s) use it.)", fixable.Count) : ""));
            var gone = needs.Where(n => n.Unavailable.Count > 0).ToList();
            if (gone.Count > 0)
                Add(AreaMods, CheckLevel.Info, L.F("{0} mod(s) use game code this version of Nomad Drive doesn't have.", gone.Count),
                    ListMods(gone, n => n.Unavailable) + "\n\n" + L.T("These mods were probably made for an older or newer version of the game. They may still work if they don't reach that code; if one misbehaves, look for an update."),
                    CheckFix.OpenInstalled);
        }

        var crashLogs = Directory.EnumerateFiles(game.Root, "preloader_*.log").ToList();
        if (crashLogs.Count > 0)
        {
            var newest = crashLogs.OrderByDescending(File.GetLastWriteTime).First();
            var log = LogAnalyzer.BepInExLog(game);
            var lastGood = File.Exists(log) ? File.GetLastWriteTime(log) : DateTime.MinValue;
            if (File.GetLastWriteTime(newest) > lastGood)
            {
                var text = SafeRead(newest);
                Add(AreaNomad, CheckLevel.Error, L.T("BepInEx crashed while starting last time."),
                    FirstLines(text, 6), text.Contains("GetPEKind") || text.Contains("MissingMethodException") ? CheckFix.InstallDotNetFix : CheckFix.RepairBepInEx,
                    crashLogs.Where(l => File.GetLastWriteTime(l) > lastGood));
            }
            else
                Add(AreaNomad, CheckLevel.Info, L.F("{0} old BepInEx crash log(s) from before it last worked.", crashLogs.Count),
                    L.T("BepInEx has started fine since. They can be cleared away."), CheckFix.ClearOldCrashLogs, crashLogs);
        }

        if (!CanWrite(game.BepInExDir.Length > 0 && Directory.Exists(game.BepInExDir) ? game.BepInExDir : game.Root))
            Add(AreaNomad, CheckLevel.Error, L.T("This app can't write to the game folder."),
                L.T("Windows is blocking changes there (common under Program Files). Run Nomad Mod Downloader as administrator, or move the Steam library outside Program Files."));

        // ---------------- Mods (from the last session's log) ----------------
        if (status.State is BepInExState.Ready or BepInExState.NeedsFirstRun)
        {
            var report = LogAnalyzer.Analyze(game, manifest);
            if (!report.BepInExRan)
                Add(AreaMods, CheckLevel.Info, L.T("No game session to check yet."), L.T("Play once with mods, then check again."), CheckFix.Play);
            else
            {
                if (!report.ChainloaderFinished)
                    Add(AreaMods, CheckLevel.Error, L.T("BepInEx stopped part way through loading mods last time."),
                        L.T("A mod probably crashed it. The Problems tab shows the error."), CheckFix.OpenProblems);
                else
                    Add(AreaMods, CheckLevel.Ok, L.F("Last session: BepInEx loaded {0} mod(s).", report.LoadedPlugins.Count));

                var errors = report.Problems.Where(p => p.Level >= ProblemLevel.Error).ToList();
                if (errors.Count > 0)
                    Add(AreaMods, errors.Any(p => p.Level == ProblemLevel.Crash) ? CheckLevel.Error : CheckLevel.Warning,
                        L.F("{0} mod problem(s) in the last session.", errors.Count),
                        string.Join("\n", errors.Take(6).Select(p => "• " + (p.ModName ?? p.Plugin ?? L.T("Game")) + ": " + p.Title)), CheckFix.OpenProblems);

                // Which installed mods didn't start, and why (the Installed tab has the details and fixes).
                try
                {
                    var ctx = ModLoadDoctor.Gather(game, manifest);
                    var entries = manifest.Mods.Where(m => m.Id != "bepinex").Select(m => (Name: m.Name, Files: (IEnumerable<string>)m.Files, Off: m.Disabled))
                        .Concat(ModInstaller.FindUntracked(game, manifest).Where(u => !BepInEx6Support.IsAnyCompatFolder(u.Rel))
                            .Select(u => (Name: Path.GetFileNameWithoutExtension(u.Rel), Files: (IEnumerable<string>)new[] { u.Rel }, Off: u.Disabled)));
                    var notStarted = entries.Select(e => (e.Name, D: ModLoadDoctor.Diagnose(ctx, e.Files, e.Off))).Where(x => x.D.State == LoadState.DidntStart).ToList();
                    if (notStarted.Count > 0)
                        Add(AreaMods, CheckLevel.Error, L.F("{0} installed mod(s) didn't start.", notStarted.Count),
                            string.Join("\n", notStarted.Select(x => "• " + x.Name + ": " + x.D.Short)) + "\n\n" + L.T("The Installed tab shows why for each one, with a fix."), CheckFix.OpenInstalled);
                }
                catch { }

                var gameDll = Path.Combine(game.DataFolder, "Managed", "Assembly-CSharp.dll");
                if (report.LogTime is { } lt && File.Exists(gameDll) && File.GetLastWriteTime(gameDll) > lt)
                    Add(AreaMods, CheckLevel.Info, L.T("Nomad Drive has updated since mods last ran."), L.T("Some mods break after game updates. Play once, then check again."), CheckFix.Play);
            }
        }

        if (Directory.Exists(game.PluginsDir))
        {
            var dlls = Directory.EnumerateFiles(game.PluginsDir, "*.dll", SearchOption.AllDirectories).ToList();

            var dupes = dlls.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            if (dupes.Count > 0)
                Add(AreaMods, CheckLevel.Warning, L.F("{0} mod file(s) are installed more than once.", dupes.Count),
                    L.T("Two copies of the same mod can conflict. Remove the older one from the Installed tab:\n") +
                    string.Join("\n", dupes.SelectMany(g => g).Select(p => "• " + Rel(game, p))), CheckFix.None, dupes.SelectMany(g => g));

            var nested = Directory.EnumerateDirectories(game.PluginsDir, "plugins", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(Path.GetDirectoryName(d)) is { } p && p.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (nested.Count > 0)
                Add(AreaMods, CheckLevel.Warning, L.T("A mod was unpacked into the wrong folder."),
                    L.T("Its files are inside an extra BepInEx\\plugins folder, so BepInEx may not load them:\n") + string.Join("\n", nested.Select(d => "• " + Rel(game, d))),
                    CheckFix.FlattenNested, nested);

            var archives = Directory.EnumerateFiles(game.PluginsDir, "*", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(f, @"\.(zip|rar|7z)$", RegexOptions.IgnoreCase)).ToList();
            if (archives.Count > 0)
                Add(AreaMods, CheckLevel.Warning, L.T("There are mod archives that were never unpacked."),
                    L.T("BepInEx can't load .zip, .rar or .7z files. Install them with the app (drag them onto it) instead:\n") + string.Join("\n", archives.Select(a => "• " + Rel(game, a))), CheckFix.None, archives);

            var zero = dlls.Where(f => { try { return new FileInfo(f).Length == 0; } catch { return false; } }).ToList();
            if (zero.Count > 0)
                Add(AreaBreakages, CheckLevel.Warning, L.F("{0} mod file(s) are empty (0 bytes).", zero.Count),
                    L.T("Usually an antivirus program emptied them. The fix moves them out of the way; reinstall those mods afterwards:\n") + string.Join("\n", zero.Select(z => "• " + Rel(game, z))),
                    CheckFix.RemoveEmptyFiles, zero);
        }

        // ---------------- Common problems ----------------
        var melon = MelonLoaderBits.Select(n => Path.Combine(game.Root, n)).Where(Directory.Exists).ToList();
        var melonProxy = Path.Combine(game.Root, "version.dll");
        var melonFound = Directory.Exists(Path.Combine(game.Root, "MelonLoader"));
        if (melonFound && File.Exists(melonProxy)) melon.Add(melonProxy);
        if (melonFound)
            Add(AreaBreakages, CheckLevel.Warning, L.T("MelonLoader leftovers are still in the game folder."),
                L.T("Running MelonLoader and BepInEx side by side breaks mods. The fix moves these to a backup (nothing is deleted):\n") + string.Join("\n", melon.Select(m => "• " + Rel(game, m))),
                CheckFix.RemoveMelonLoader, melon);

        var others = OtherProxies.Select(n => Path.Combine(game.Root, n)).Where(File.Exists).Where(p => !(melonFound && p == melonProxy)).ToList();
        if (others.Count > 0)
            Add(AreaBreakages, CheckLevel.Warning, L.T("Another mod loader's starter file is next to the game."),
                L.T("These files load other mod frameworks and can stop BepInEx from starting. The fix moves them to a backup:\n") + string.Join("\n", others.Select(o => "• " + Path.GetFileName(o))),
                CheckFix.RemoveOtherLoader, others);

        var blocked = BlockedFiles(game).ToList();
        if (blocked.Count > 0)
            Add(AreaBreakages, CheckLevel.Warning, L.F("Windows has marked {0} file(s) as \"downloaded from the internet\".", blocked.Count),
                L.T("Blocked files can fail to load. The fix removes the mark (like ticking Unblock in the file's Properties)."), CheckFix.Unblock, blocked);
        else
            Add(AreaBreakages, CheckLevel.Ok, L.T("No files are blocked by Windows."));

        if (!melonFound && others.Count == 0)
            Add(AreaBreakages, CheckLevel.Ok, L.T("No other mod loaders are getting in the way."));

        return list;
    }

    // ==================================================================
    // Fixes (the UI confirms each one first; the game must be closed)

    /// <summary>Applies a file fix and says what it did. BepInEx install/repair/update go through the UI's download flow.</summary>
    public static string Apply(CheckItem item, GameInstall game)
    {
        switch (item.Fix)
        {
            case CheckFix.WriteDoorstop:
                WriteDoorstop(game);
                return L.T("Wrote a working doorstop_config.ini.");
            case CheckFix.EnableDoorstop:
                File.WriteAllText(DoorstopIni(game), Regex.Replace(SafeRead(DoorstopIni(game)), @"(?im)^(\s*enabled\s*=\s*)false", "${1}true"));
                return L.T("Switched BepInEx back on.");
            case CheckFix.InstallCompat:
            {
                var w = BepInExCompat.Install(game);
                return L.F("Installed BepInEx 5 support ({0} files).", w.Count);
            }
            case CheckFix.InstallCompat6:
            {
                var w = BepInEx6Support.Install(game);
                return L.F("Installed BepInEx 6 support ({0} files).", w.Count);
            }
            case CheckFix.InstallDotNetFix:
                InstallDotNetFix(game);
                ClearCrashLogs(game, item.Paths);
                return L.T("Set up Nomad Drive's .NET fix (DotNetFix).");
            case CheckFix.EnableDiskLog:
            {
                var cfg = Cfg(game);
                var text = SafeRead(cfg);
                var m = Regex.Match(text, @"(?ims)^\[Logging\.Disk\](.*?)(?=^\[|\z)");
                if (m.Success)
                {
                    var fixedSection = Regex.Replace(m.Value, @"(?im)^(\s*Enabled\s*=\s*)false", "${1}true");
                    File.WriteAllText(cfg, text.Remove(m.Index, m.Length).Insert(m.Index, fixedSection));
                }
                return L.T("Switched BepInEx's log file back on.");
            }
            case CheckFix.Unblock:
            {
                var n = 0;
                foreach (var f in item.Paths) { try { File.Delete(f + ":Zone.Identifier"); n++; } catch { } }
                return L.F("Unblocked {0} file(s).", n);
            }
            case CheckFix.RemoveMelonLoader:
            case CheckFix.RemoveOtherLoader:
            case CheckFix.RemoveEmptyFiles:
            case CheckFix.ClearOldCrashLogs:
            {
                var dest = MoveToBackup(game, item.Paths, item.Fix.ToString());
                return L.F("Moved {0} item(s) to {1}", item.Paths.Count, dest);
            }
            case CheckFix.FlattenNested:
            {
                foreach (var nested in item.Paths) Flatten(game, nested);
                return L.F("Moved {0} mod(s) into the right folder.", item.Paths.Count);
            }
        }
        return "";
    }

    /// <summary>Once the cause of a startup crash is fixed, its crash log goes to a backup so it isn't reported again.</summary>
    public static void ClearCrashLogs(GameInstall game, IEnumerable<string> paths)
    {
        var logs = paths.Where(p => Path.GetFileName(p).StartsWith("preloader_", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (logs.Count > 0) MoveToBackup(game, logs, "crash-logs");
    }

    /// <summary>Fixes of the same kind merged into one (e.g. two checks that both need the .NET fix).</summary>
    public static List<CheckItem> Merge(IEnumerable<CheckItem> items) =>
        items.GroupBy(i => i.Fix).Select(g => new CheckItem
        {
            Area = g.First().Area, Level = g.Max(i => i.Level), Title = g.First().Title, Detail = g.First().Detail, Fix = g.Key,
            Paths = g.SelectMany(i => i.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }).ToList();

    /// <summary>
    /// Makes a BepInEx install work on Nomad Drive: if the game's mscorlib is stripped, unpacks the full Mono
    /// libraries into DotNetFix and points doorstop at them. Safe to call after every install or repair.
    /// </summary>
    public static bool EnsureNomadSetup(GameInstall game)
    {
        if (CorlibIsStripped(GameCorlib(game)) != true) return false;
        if (DotNetFixDir(game) is { } dir && CorlibIsStripped(Path.Combine(dir, "mscorlib.dll")) == false
            && AssemblyVersion(Path.Combine(dir, "netstandard.dll")) >= new Version(2, 1) && File.Exists(Path.Combine(dir, "Mono.Security.dll"))) return false;
        InstallDotNetFix(game);
        return true;
    }

    /// <summary>The highest .NET Standard the game can load: netstandard.dll in the .NET fix folder or the game's Managed folder.</summary>
    public static Version? NetStandardAvailable(GameInstall game)
    {
        var dirs = new List<string>();
        if (DotNetFixDir(game) is { } d) dirs.Add(d);
        dirs.Add(Path.Combine(game.DataFolder, "Managed"));
        return dirs.Select(x => AssemblyVersion(Path.Combine(x, "netstandard.dll"))).Where(v => v != null).Max();
    }

    /// <summary>Enabled mods built for .NET Standard, with the version each needs.</summary>
    public static List<(string Name, Version Version)> ModsNeedingNetStandard(GameInstall game)
    {
        var list = new List<(string, Version)>();
        if (!Directory.Exists(game.PluginsDir)) return list;
        foreach (var dll in Directory.EnumerateFiles(game.PluginsDir, "*.dll", SearchOption.AllDirectories))
        {
            if (NetStandardNeeded(dll) is not { } v) continue;
            var name = PluginInspector.Read(dll).Select(p => p.Name).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(dll);
            list.Add((name, v));
        }
        return list;
    }

    /// <summary>The netstandard version a DLL references, or null if it doesn't use .NET Standard.</summary>
    public static Version? NetStandardNeeded(string dll)
    {
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                if (md.GetString(r.Name) == "netstandard") return r.Version;
            }
        }
        catch { }
        return null;
    }

    public static Version? AssemblyVersion(string dll)
    {
        try
        {
            if (!File.Exists(dll)) return null;
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            return pe.HasMetadata ? pe.GetMetadataReader().GetAssemblyDefinition().Version : null;
        }
        catch { return null; }
    }

    public static void InstallDotNetFix(GameInstall game)
    {
        var dir = Path.Combine(game.Root, DotNetFixFolder);
        Directory.CreateDirectory(dir);
        using (var s = typeof(BepInExChecker).Assembly.GetManifestResourceStream("Assets.DotNetFix.zip")
                       ?? throw new InvalidOperationException("The app is missing its DotNetFix files."))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains("..")))
                e.ExtractToFile(Path.Combine(dir, e.Name), true);

        var ini = DoorstopIni(game);
        if (!File.Exists(ini)) { WriteDoorstop(game); return; }
        var text = SafeRead(ini);
        var current = (IniValue(text, "dll_search_path_override") ?? "").Trim().Trim('"');
        if (Regex.IsMatch(text, @"(?im)^\s*dll_search_path_override\s*="))
        {
            // Keep whatever else is there (BepInEx 6 puts BepInEx\core here); the full libraries go first.
            var paths = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !p.StartsWith("#") && !p.Equals(DotNetFixFolder, StringComparison.OrdinalIgnoreCase)).Prepend(DotNetFixFolder);
            var value = string.Join(";", paths);
            text = Regex.Replace(text, @"(?im)^([ \t]*dll_search_path_override[ \t]*=)[^\r\n]*", "${1} " + (value.Contains(';') ? "\"" + value + "\"" : value));
        }
        else if (Regex.IsMatch(text, @"(?im)^\[UnityMono\]"))
            text = Regex.Replace(text, @"(?im)^\[UnityMono\]\s*$", "[UnityMono]\r\n\r\n# Nomad Drive's mscorlib is stripped: use the full Mono libraries\r\ndll_search_path_override = " + DotNetFixFolder, RegexOptions.None, TimeSpan.FromSeconds(1));
        else
            text = text.TrimEnd() + "\r\n\r\n[UnityMono]\r\n\r\ndll_search_path_override = " + DotNetFixFolder + "\r\n";
        File.WriteAllText(ini, text);
    }

    /// <summary>A doorstop 4 config for BepInEx 5 on Nomad Drive.</summary>
    public static void WriteDoorstop(GameInstall game)
    {
        var needsFix = CorlibIsStripped(GameCorlib(game)) == true;
        var major = BepInExManager.MajorOf(game);
        var compat = UnityCompat.PatcherInstalled(game) ? UnityCompat.CompatFolder : null;
        // BepInEx 6 also needs its core folder on the path; BepInEx 5 finds its own.
        var parts = new List<string>();
        if (needsFix) parts.Add(DotNetFixFolder);
        if (compat != null) parts.Add(compat);
        if (major == 6) parts.Add("BepInEx\\core");
        var search = parts.Count > 1 ? "\"" + string.Join(";", parts) + "\"" : string.Join(";", parts);
        var text = "# Written by Nomad Mod Downloader\r\n[General]\r\n\r\n# Enable Doorstop?\r\nenabled = true\r\n\r\n# Path to the assembly to load and execute\r\ntarget_assembly = " + PreloaderFor(major) + "\r\n\r\n" +
                   "redirect_output_log = false\r\n\r\nboot_config_override =\r\n\r\nignore_disable_switch = false\r\n\r\n[UnityMono]\r\n\r\n" +
                   (needsFix ? "# Nomad Drive's mscorlib is stripped: use the full Mono libraries\r\n" : "") + "dll_search_path_override = " + search +
                   "\r\n\r\ndebug_enabled = false\r\n\r\ndebug_address = 127.0.0.1:10000\r\n\r\ndebug_suspend = false\r\n";
        File.WriteAllText(DoorstopIni(game), text);
        if (needsFix && DotNetFixDir(game) == null) InstallDotNetFix(game);
    }

    // ==================================================================
    // Helpers

    /// <summary>True if mscorlib is missing Module.GetPEKind (code-stripped), false if it's complete, null if unreadable.</summary>
    public static bool? CorlibIsStripped(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != "Module" || md.GetString(t.Namespace) != "System.Reflection") continue;
                return !t.GetMethods().Any(mh => md.GetString(md.GetMethodDefinition(mh).Name) == "GetPEKind");
            }
            return true;
        }
        catch { return null; }
    }

    /// <summary>The folder doorstop puts first on the search path, if it's set and exists.</summary>
    public static string? DotNetFixDir(GameInstall game)
    {
        var v = IniValue(SafeRead(DoorstopIni(game)), "dll_search_path_override") ?? IniValue(SafeRead(DoorstopIni(game)), "dllSearchPathOverride");
        if (string.IsNullOrWhiteSpace(v)) return null;
        var dirs = v.Trim().Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormPath).Select(p => Path.IsPathRooted(p) ? p : Path.Combine(game.Root, p)).Where(Directory.Exists).ToList();
        return dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "mscorlib.dll")))
            ?? dirs.FirstOrDefault(d => Path.GetFileName(d).Equals(DotNetFixFolder, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> BlockedFiles(GameInstall game)
    {
        var roots = new List<string> { Path.Combine(game.Root, "winhttp.dll") };
        foreach (var sub in new[] { "core", "plugins", "patchers" })
        {
            var d = Path.Combine(game.BepInExDir, sub);
            if (Directory.Exists(d)) roots.AddRange(Directory.EnumerateFiles(d, "*.dll", SearchOption.AllDirectories));
        }
        foreach (var f in roots.Where(File.Exists))
        {
            bool marked;
            try { marked = File.Exists(f + ":Zone.Identifier"); } catch { marked = false; }
            if (marked) yield return f;
        }
    }

    private static Machine? MachineOf(string path)
    {
        try { using var fs = File.OpenRead(path); using var pe = new PEReader(fs); return pe.PEHeaders.CoffHeader.Machine; }
        catch { return null; }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".nmd-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string MoveToBackup(GameInstall game, IEnumerable<string> paths, string label)
    {
        var dest = Path.Combine(AppPaths.Backups, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}");
        foreach (var p in paths)
        {
            var rel = Path.GetRelativePath(game.Root, p);
            var to = Path.Combine(dest, rel.StartsWith("..") ? Path.GetFileName(p) : rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Directory.Exists(p)) MoveDir(p, to);
            else if (File.Exists(p)) File.Move(p, to, true);
        }
        return dest;
    }

    /// <summary>Directory.Move fails across drives (game on D:, backups on C:), so copy then delete.</summary>
    private static void MoveDir(string from, string to)
    {
        try { Directory.Move(from, to); return; } catch (IOException) { }
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
        Directory.Delete(from, true);
    }

    /// <summary>plugins/Mod/BepInEx/plugins/* → plugins/Mod/*, and plugins/Mod/BepInEx/config/* → BepInEx/config (if not already there).</summary>
    private static void Flatten(GameInstall game, string nestedPlugins)
    {
        var nestedBep = Path.GetDirectoryName(nestedPlugins)!;
        var modDir = Path.GetDirectoryName(nestedBep)!;
        foreach (var f in Directory.EnumerateFiles(nestedPlugins, "*", SearchOption.AllDirectories).ToList())
        {
            var to = Path.Combine(modDir, Path.GetRelativePath(nestedPlugins, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(f, to, true);
        }
        var nestedCfg = Path.Combine(nestedBep, "config");
        if (Directory.Exists(nestedCfg))
            foreach (var f in Directory.EnumerateFiles(nestedCfg, "*", SearchOption.AllDirectories).ToList())
            {
                var to = Path.Combine(game.BepInExDir, "config", Path.GetRelativePath(nestedCfg, f));
                if (File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(f, to);
            }
        try { if (!Directory.EnumerateFiles(nestedBep, "*", SearchOption.AllDirectories).Any()) Directory.Delete(nestedBep, true); } catch { }
    }

    private static string NormPath(string p) => p.Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string Rel(GameInstall g, string p) => Path.GetRelativePath(g.Root, p);

    private static string SafeRead(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var r = new StreamReader(fs); return r.ReadToEnd(); }
        catch { return ""; }
    }

    private static string? IniValue(string text, string key)
    {
        // [ \t] not \s: an empty value mustn't run on into the next line (it used to pick up the comment below it).
        var m = Regex.Match(text, @"(?im)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(.*?)[ \t\r]*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Section(string text, string name)
    {
        var m = Regex.Match(text, @"(?ims)^\[" + Regex.Escape(name) + @"\](.*?)(?=^\[|\z)");
        return m.Success ? m.Value : "";
    }

    private static string FirstLines(string text, int n) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).Take(n));
}
