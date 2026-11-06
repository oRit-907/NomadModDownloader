using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

/// <summary>How a mod did at the last game start.</summary>
public enum LoadState
{
    Started,          // BepInEx loaded every plugin in it
    StartedWithErrors,
    DidntStart,
    InstalledSince,   // installed or changed after the game last started
    NoSession,        // the game hasn't run with BepInEx yet
    Off,              // disabled
    SupportFiles,     // DLLs with no plugin in them (libraries other mods use)
}

/// <summary>What the "Fix" button does for a mod that didn't start.</summary>
public enum LoadFix
{
    None,
    EnableRequirement,  // a mod it needs is installed but switched off: switch it on
    FindRequirement,    // a mod it needs isn't installed: search Nexus for it
    DisableThis,        // a duplicate, a MelonLoader mod, or a clash: switch this one off
    Unblock,
    Reinstall,
    UpdateThis,         // a newer version may fix it
    OpenChecker,        // BepInEx itself has a problem (or the BepInEx 5 compatibility layer is missing)
    OpenProblems,
    OpenLog,
    Play,
}

public sealed class LoadDiagnosis
{
    public LoadState State { get; init; }
    public string Short { get; init; } = "";      // for the list column
    public string Why { get; init; } = "";        // plain-words reason
    public string HowToFix { get; init; } = "";
    public LoadFix Fix { get; init; }
    public string? FixTarget { get; init; }       // requirement name/GUID, or the mod to enable
    public string? FixTargetRel { get; init; }    // where that mod is (BepInEx/plugins/...)
    public List<string> Plugins { get; init; } = new();
    public List<string> Files { get; init; } = new();
}

/// <summary>
/// Works out, for each installed mod, whether it started at the last game start and, if it didn't, why and how
/// to fix it: from BepInEx's LogOutput.log plus what's inside the mod's DLLs.
/// </summary>
public static class ModLoadDoctor
{
    private static readonly Regex HotStarted = new(@"Started without a restart: (.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The facts every diagnosis needs, gathered once.</summary>
    public sealed class Context
    {
        public required GameInstall Game { get; init; }
        public required SessionReport Report { get; init; }
        public required HashSet<string> Loaded { get; init; }           // plugin names BepInEx (or the in-game menu) started
        public required List<PluginInfo> Enabled { get; init; }          // plugins in enabled DLLs
        public required List<PluginInfo> Disabled { get; init; }         // plugins in .dll.disabled files
        public required HashSet<string> Assemblies { get; init; }        // every assembly name the game can find
        public int BepInExMajor { get; init; }                           // 5 or 6
        public bool HasCompat5 { get; init; }                            // BepInEx 6 with the BepInEx 5 compatibility layer
        public Version? NetStandard { get; init; }                       // highest .NET Standard the game can load
        public bool UnityCompatReady { get; init; }                      // Unity Compatibility installed (restores stripped Unity code)
        public DateTime? UnityCompatSince { get; init; }                 // when its patcher was installed
        public Lazy<GameApiScanner.GameApi?> GameApi { get; init; } = new(() => null);
        public Lazy<GameApiScanner.GameApi?> FullApi { get; init; } = new(() => null);
        public DateTime? LogTime => Report.LogTime;
    }

    public static Context Gather(GameInstall game, Manifest manifest)
    {
        var report = LogAnalyzer.Analyze(game, manifest);
        var loaded = new HashSet<string>(report.LoadedPlugins, StringComparer.OrdinalIgnoreCase);
        try
        {
            var log = LogAnalyzer.BepInExLog(game);
            if (File.Exists(log))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var text = new StreamReader(fs).ReadToEnd();
                foreach (Match m in HotStarted.Matches(text))
                    foreach (var part in m.Groups[1].Value.Split(", "))
                    {
                        var sp = part.Trim().LastIndexOf(' ');
                        loaded.Add(sp > 0 ? part.Trim()[..sp] : part.Trim());
                    }
            }
        }
        catch { }

        var all = Directory.Exists(game.PluginsDir) ? Directory.EnumerateFiles(game.PluginsDir, "*", SearchOption.AllDirectories).ToList() : new();
        var enabled = all.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).SelectMany(PluginInspector.Read).ToList();
        var disabled = all.Where(f => f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)).SelectMany(PluginInspector.Read).ToList();

        var asm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string dir, bool deep = false)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.dll", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)) asm.Add(Path.GetFileNameWithoutExtension(f));
        }
        AddDir(Path.Combine(game.DataFolder, "Managed"));
        AddDir(Path.Combine(game.BepInExDir, "core"));
        AddDir(Path.Combine(game.BepInExDir, "patchers"), true);
        AddDir(game.PluginsDir, true);
        AddDir(BepInExCompat.RuntimeDir(game));
        AddDir(Path.Combine(game.Root, BepInExChecker.DotNetFixFolder));
        AddDir(Path.Combine(game.Root, "MonoBleedingEdge", "lib", "mono", "4.5"));
        AddDir(Path.Combine(game.Root, "MonoBleedingEdge", "lib", "mono", "4.5", "Facades"));
        foreach (var n in new[] { "mscorlib", "System", "System.Core" }) asm.Add(n);
        var ns = BepInExChecker.NetStandardAvailable(game);
        if (ns != null) asm.Add("netstandard");

        var major = BepInExManager.MajorOf(game);
        return new Context
        {
            Game = game, Report = report, Loaded = loaded, Enabled = enabled, Disabled = disabled, Assemblies = asm,
            BepInExMajor = major, HasCompat5 = major == 6 && BepInExCompat.IsInstalled(game), NetStandard = ns,
            UnityCompatReady = UnityCompat.IsInstalled(game),
            UnityCompatSince = UnityCompat.PatcherInstalled(game) ? File.GetLastWriteTime(UnityCompat.PatcherDll(game)) : null,
            GameApi = new(() => Directory.Exists(UnityCompat.ManagedDir(game)) ? new GameApiScanner.GameApi(UnityCompat.ManagedDir(game)) : null),
            FullApi = new(() => UnityCompat.LibsInstalled(game) ? new GameApiScanner.GameApi(UnityCompat.ManagedDir(game), UnityCompat.LibsDir(game)!) : null),
        };
    }

    /// <summary>What the mod uses that the game's files lack: restorable by Unity Compatibility, or not available at all.</summary>
    public static (List<GameApiScanner.Missing> Restorable, List<GameApiScanner.Missing> Unavailable) UnityNeeds(Context ctx, IEnumerable<string> dlls)
    {
        var restorable = new List<GameApiScanner.Missing>();
        var unavailable = new List<GameApiScanner.Missing>();
        var api = ctx.GameApi.Value;
        if (api == null) return (restorable, unavailable);
        foreach (var dll in dlls)
        {
            List<GameApiScanner.Missing> missing;
            try { missing = GameApiScanner.Scan(dll, api); } catch { continue; }
            if (missing.Count == 0) continue;
            HashSet<string>? still = null;
            if (ctx.FullApi.Value is { } full)
                try { still = GameApiScanner.Scan(dll, full).Select(m => m.Assembly + "|" + m.Display).ToHashSet(); } catch { }
            foreach (var m in missing)
                (m.IsUnity && (still == null || !still.Contains(m.Assembly + "|" + m.Display)) ? restorable : unavailable).Add(m);
        }
        return (restorable, unavailable);
    }

    /// <param name="files">The mod's files, relative to the game folder ('/' separated), as the manifest stores them.</param>
    public static LoadDiagnosis Diagnose(Context ctx, IEnumerable<string> files, bool disabled)
    {
        var game = ctx.Game;
        var dlls = files.Where(ModInstaller.IsLoadableDll).Select(r => SafeFull(game, r)).Where(f => f != null).Cast<string>().ToList();
        // A folder entry (mods added by hand): every DLL inside.
        foreach (var rel in files)
        {
            var full = SafeFull(game, rel);
            if (full != null && Directory.Exists(full))
                dlls.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)));
        }
        // The real files on disk now (a disabled mod's DLLs end in .disabled).
        var present = dlls.SelectMany(d => new[] { d, d + ".disabled" }).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var plugins = present.SelectMany(PluginInspector.Read).ToList();

        if (disabled || (present.Count > 0 && present.All(p => p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))))
            return new LoadDiagnosis { State = LoadState.Off, Short = L.T("Off"), Why = L.T("This mod is switched off, so BepInEx skips it."), HowToFix = L.T("Press Enable to switch it back on."), Plugins = Names(plugins), Files = present };

        var loadable = present.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (loadable.Count == 0)
            return new LoadDiagnosis { State = LoadState.DidntStart, Short = L.T("Files missing"), Why = L.T("The mod's DLL files aren't there any more. An antivirus program may have removed them."), HowToFix = L.T("Reinstall the mod. If it disappears again, add the game folder to your antivirus's exclusions."), Fix = LoadFix.Reinstall, Files = present };

        var empty = loadable.Where(f => new FileInfo(f).Length == 0).ToList();
        if (empty.Count > 0)
            return new LoadDiagnosis { State = LoadState.DidntStart, Short = L.T("File emptied"), Why = L.T("A file of this mod is empty (0 bytes), usually because an antivirus program blocked it."), HowToFix = L.T("Reinstall the mod. If it happens again, add the game folder to your antivirus's exclusions."), Fix = LoadFix.Reinstall, Files = empty };

        if (plugins.Count == 0)
        {
            var melon = loadable.SelectMany(References).Any(r => r.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase));
            return new LoadDiagnosis
            {
                State = LoadState.SupportFiles, Short = L.T("Support files"), Files = loadable,
                Why = melon ? L.T("This is a MelonLoader mod. BepInEx can't run MelonLoader mods, so it does nothing here. Look for a BepInEx version and switch this one off.")
                            : L.T("These are support files other mods use. There's nothing in them for BepInEx to start, which is normal."),
                Fix = melon ? LoadFix.DisableThis : LoadFix.None,
            };
        }

        var names = Names(plugins);
        if (!ctx.Report.BepInExRan)
            return new LoadDiagnosis { State = LoadState.NoSession, Short = L.T("Not run yet"), Why = L.T("The game hasn't been started with BepInEx yet, so there's nothing to check."), HowToFix = L.T("Play the game once to the main menu, then come back."), Fix = LoadFix.Play, Plugins = names, Files = loadable };

        var started = names.Where(n => ctx.Loaded.Contains(n)).ToList();

        // .NET Standard: without netstandard.dll the mod can't load, however recently it was installed.
        var needNs = loadable.Select(BepInExChecker.NetStandardNeeded).Where(v => v != null).Max();
        if (started.Count < names.Count && needNs != null && (ctx.NetStandard == null || ctx.NetStandard < needNs))
            return new LoadDiagnosis
            {
                State = LoadState.DidntStart, Short = L.F("Needs .NET Standard {0}", needNs.ToString(2)), Fix = LoadFix.OpenChecker, Plugins = names, Files = loadable,
                Why = L.F("This mod is built for .NET Standard {0}, and Nomad Drive doesn't include the library it needs (netstandard.dll).", needNs.ToString(2)),
                HowToFix = L.T("Open Check BepInEx and set up .NET Standard support."),
            };

        // Unity code Nomad Drive's build stripped (MissingMethodException when the mod calls it).
        var errors = ctx.Report.Problems.Where(p => p.Level >= ProblemLevel.Error && p.Plugin != null && plugins.Any(x => Same(x, p.Plugin))).ToList();
        if (started.Count < names.Count || errors.Count > 0)
        {
            var (restorable, _) = UnityNeeds(ctx, loadable);
            if (restorable.Count > 0)
            {
                var what = string.Join(", ", restorable.Take(3).Select(m => m.Display.Replace("UnityEngine.", ""))) + (restorable.Count > 3 ? L.F(" and {0} more", restorable.Count - 3) : "");
                var state = started.Count < names.Count ? LoadState.DidntStart : LoadState.StartedWithErrors;
                if (!ctx.UnityCompatReady)
                    return new LoadDiagnosis
                    {
                        State = state, Short = L.T("Needs Unity Compatibility"), Fix = LoadFix.OpenChecker, Plugins = names, Files = loadable,
                        Why = L.F("It uses Unity code that Nomad Drive's build left out: {0}.", what),
                        HowToFix = L.T("Open Check BepInEx and set up Unity Compatibility. It puts the missing Unity code back when the game starts."),
                    };
                if (ctx.UnityCompatSince is { } since && ctx.LogTime is { } lt0 && since > lt0)
                    return new LoadDiagnosis { State = LoadState.InstalledSince, Short = L.T("Waiting for a game start"), Why = L.T("Unity Compatibility was set up after the game last started, so this mod hasn't had a chance to use it yet."), HowToFix = L.T("Play the game once, then check again."), Fix = LoadFix.Play, Plugins = names, Files = loadable };
            }
        }

        var newest = loadable.Max(File.GetLastWriteTime);
        if (started.Count < names.Count && ctx.LogTime is { } lt && newest > lt)
            return new LoadDiagnosis { State = LoadState.InstalledSince, Short = L.T("Installed since last start"), Why = L.T("This mod was installed or changed after the game last started."), HowToFix = L.T("Play the game once, then check again."), Fix = LoadFix.Play, Plugins = names, Files = loadable };

        if (started.Count < names.Count && ctx.HasCompat5 && ctx.LogTime is { } lt2 && File.GetLastWriteTime(BepInExCompat.PluginDll(game)) > lt2
            && BuiltFor(loadable.SelectMany(References)) == "5")
            return new LoadDiagnosis { State = LoadState.InstalledSince, Short = L.T("Waiting for a game start"), Why = L.T("BepInEx 5 support was added after the game last started, so this BepInEx 5 mod hasn't had a chance to load yet."), HowToFix = L.T("Play the game once, then check again."), Fix = LoadFix.Play, Plugins = names, Files = loadable };

        if (started.Count == names.Count)
        {
            if (errors.Count > 0)
                return new LoadDiagnosis { State = LoadState.StartedWithErrors, Short = L.T("Started, with errors"), Why = L.T("It started, but then hit an error: ") + errors[0].Title, HowToFix = L.T("If the mod isn't working, check for an update, or turn it off and tell its author (Problems has the details to send)."), Fix = LoadFix.OpenProblems, Plugins = names, Files = loadable };
            return new LoadDiagnosis { State = LoadState.Started, Short = L.T("Started"), Why = L.T("BepInEx started this mod at the last game start."), Plugins = names, Files = loadable };
        }

        // ---- It didn't start: find out why ----
        LoadDiagnosis No(string shortText, string why, string how, LoadFix fix = LoadFix.None, string? target = null, string? targetRel = null) =>
            new() { State = LoadState.DidntStart, Short = shortText, Why = why, HowToFix = how, Fix = fix, FixTarget = target, FixTargetRel = targetRel, Plugins = names, Files = loadable };

        if (!ctx.Report.ChainloaderFinished && started.Count == 0)
            return No(L.T("BepInEx stopped early"), L.T("BepInEx stopped before it got to this mod, usually because another mod (or BepInEx itself) crashed."), L.T("Open Check BepInEx to find what stopped it."), LoadFix.OpenChecker);

        // Wrong mod loader / wrong BepInEx
        var refs = loadable.SelectMany(References).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Any(r => r.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase)))
            return No(L.T("MelonLoader mod"), L.T("This is a MelonLoader mod. BepInEx can't run MelonLoader mods."), L.T("Look for a BepInEx version of it, and switch this one off."), LoadFix.DisableThis);
        var builtFor = BuiltFor(refs);
        if (builtFor == "il2cpp")
            return No(L.T("Made for IL2CPP games"), L.T("This mod was made for IL2CPP games. Nomad Drive is a Mono game, so it can't run here."), L.T("Make sure it's a Nomad Drive mod, and switch this one off."), LoadFix.DisableThis);
        if (builtFor == "6" && ctx.BepInExMajor == 5)
            return No(L.T("Made for BepInEx 6"), L.T("This mod was built for BepInEx 6, but BepInEx 5 is installed."),
                L.T("Upgrade to BepInEx 6 in Check BepInEx. Your BepInEx 5 mods keep working there through the BepInEx 5 compatibility layer."), LoadFix.OpenChecker);
        if (builtFor == "5" && ctx.BepInExMajor == 6 && !ctx.HasCompat5)
            return No(L.T("Needs BepInEx 5 support"), L.T("This mod was built for BepInEx 5. BepInEx 6 runs those through the BepInEx 5 compatibility layer, which isn't installed."),
                L.T("Open Check BepInEx and press Install BepInEx 5 support."), LoadFix.OpenChecker);

        // Requirements
        var mine = new HashSet<string>(plugins.Select(p => p.Guid), StringComparer.OrdinalIgnoreCase);
        var missing = plugins.SelectMany(p => p.HardDependencies).Where(d => !mine.Contains(d) && !ctx.Enabled.Any(e => e.Guid.Equals(d, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var d in missing)
        {
            var off = ctx.Disabled.FirstOrDefault(x => x.Guid.Equals(d, StringComparison.OrdinalIgnoreCase));
            if (off != null)
                return No(L.F("Needs {0} (off)", off.Name), L.F("It needs {0}, which is installed but switched off.", off.Name), L.F("Switch {0} back on.", off.Name), LoadFix.EnableRequirement, off.Name, Rel(game, off.File));
        }
        if (missing.Count > 0)
            return No(L.F("Needs {0}", Pretty(missing[0])), L.F("It needs another mod that isn't installed: {0}.", string.Join(", ", missing.Select(Pretty))),
                L.T("Install that mod first. The mod's Nexus page usually lists it under Requirements."), LoadFix.FindRequirement, Pretty(missing[0]));
        // A BepInEx 5 mod needing a BepInEx 6 mod (or the other way round) can't see it: the two loaders are separate.
        var crossNeeds = plugins.SelectMany(p => p.HardDependencies).Where(d => !mine.Contains(d))
            .Select(d => ctx.Enabled.FirstOrDefault(e => e.Guid.Equals(d, StringComparison.OrdinalIgnoreCase))).Where(e => e != null)
            .FirstOrDefault(e => BuiltFor(References(e!.File)) is { } other && builtFor != null && other != builtFor);
        if (crossNeeds != null && ctx.HasCompat5)
            return No(L.F("Needs {0} (other BepInEx)", crossNeeds.Name), L.F("It needs {0}, which is built for a different BepInEx version, so the two can't see each other.", crossNeeds.Name),
                L.F("Install versions of both mods built for the same BepInEx, or ask the authors.", crossNeeds.Name), LoadFix.UpdateThis);

        // What the log said
        var said = ctx.Report.Problems.FirstOrDefault(p => p.Plugin != null && plugins.Any(x => Same(x, p.Plugin)));
        if (said != null)
        {
            if (said.Detail.Contains("newer version exists", StringComparison.OrdinalIgnoreCase))
                return No(L.T("Installed twice"), said.Title, L.T("A newer copy of this mod is installed and is used instead. Switch this older copy off, or uninstall it."), LoadFix.DisableThis);
            if (said.Detail.Contains("incompatible", StringComparison.OrdinalIgnoreCase))
                return No(L.T("Clashes with another mod"), said.Title, L.T("These two mods can't run together. Switch one of them off."), LoadFix.DisableThis);
            if (Regex.IsMatch(said.Title + " " + said.Detail, "Missing(Method|Field)Exception|TypeLoadException")
                && UnityNeeds(ctx, loadable).Unavailable is { Count: > 0 } gone)
                return No(L.T("Made for another game version"),
                    L.F("It uses game code this version of Nomad Drive doesn't have: {0}. The game has probably changed since the mod was made.", string.Join(", ", gone.Take(3).Select(m => m.Display))),
                    L.T("Check for an update of the mod, or ask its author."), LoadFix.UpdateThis);
            return No(L.T("Error while starting"), said.Title, L.T("Check for an update. If that doesn't help, turn it off and tell its author (Problems has the details to send)."), LoadFix.OpenProblems);
        }

        // Duplicate GUID elsewhere
        var dupe = ctx.Enabled.FirstOrDefault(e => mine.Contains(e.Guid) && !loadable.Any(l => l.Equals(e.File, StringComparison.OrdinalIgnoreCase)));
        if (dupe != null)
            return No(L.T("Installed twice"), L.F("Another copy of this mod is installed at {0}, and BepInEx only starts one.", Rel(game, dupe.File)), L.T("Switch this copy off, or uninstall the one you don't want."), LoadFix.DisableThis);

        var blocked = loadable.Where(f => { try { return File.Exists(f + ":Zone.Identifier"); } catch { return false; } }).ToList();
        if (blocked.Count > 0)
            return No(L.T("Blocked by Windows"), L.T("Windows has marked this mod's files as downloaded from the internet, which can stop them loading."), L.T("Press Unblock."), LoadFix.Unblock);

        var unknown = refs.Where(r => !ctx.Assemblies.Contains(r) && !r.StartsWith("System.", StringComparison.OrdinalIgnoreCase) && r != "Microsoft.CSharp"
                                      && !(ctx.HasCompat5 && r.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            return No(L.T("Needs a missing library"), L.F("It needs files that aren't in the game: {0}. It may be made for a different game, or need another mod.", string.Join(", ", unknown.Take(4))),
                L.T("Make sure it's a Nomad Drive mod, and install anything its Nexus page lists under Requirements."), LoadFix.FindRequirement, unknown[0]);

        return No(L.T("Didn't start"), L.T("BepInEx skipped this mod without saying why. It may be made for a different game (BepInEx only starts mods meant for Nomad Drive)."),
            L.T("Check the mod is for Nomad Drive and up to date. The BepInEx log may say more."), LoadFix.OpenLog);
    }

    // ------------------------------------------------------------------

    /// <summary>"5", "6", "il2cpp" or null: which BepInEx a mod's DLL references were built against.</summary>
    public static string? BuiltFor(IEnumerable<string> refs)
    {
        var r = refs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (r.Contains("BepInEx.Unity.IL2CPP") || r.Contains("BepInEx.IL2CPP") || r.Contains("Il2CppInterop.Runtime")) return "il2cpp";
        if (r.Contains("BepInEx.Unity.Mono") || r.Contains("BepInEx.Core")) return "6";
        if (r.Contains("BepInEx")) return "5";
        return null;
    }

    /// <summary>Names of the enabled plugins in BepInEx\plugins built for one BepInEx ("5" or "6").</summary>
    public static List<string> ModsBuiltFor(GameInstall game, string major)
    {
        if (!Directory.Exists(game.PluginsDir)) return new();
        return Directory.EnumerateFiles(game.PluginsDir, "*.dll", SearchOption.AllDirectories)
            .Where(d => !BepInExCompat.IsCompatPlugin(game, d) && BuiltFor(References(d)) == major)
            .SelectMany(d => PluginInspector.Read(d).Select(p => p.Name).DefaultIfEmpty(Path.GetFileNameWithoutExtension(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> References(string dll)
    {
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return new();
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        }
        catch { return new(); }
    }

    private static bool Same(PluginInfo p, string key) => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || p.Guid.Equals(key, StringComparison.OrdinalIgnoreCase);
    private static List<string> Names(List<PluginInfo> p) => p.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"com.author.coolmod" → "coolmod" for display.</summary>
    private static string Pretty(string guid)
    {
        var parts = guid.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[^1] : guid;
    }

    private static string? SafeFull(GameInstall game, string rel) { try { return ModInstaller.SafeJoin(game.Root, rel); } catch { return null; } }
    private static string Rel(GameInstall game, string full) => Path.GetRelativePath(game.Root, full).Replace('\\', '/');
}
