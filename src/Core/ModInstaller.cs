using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NomadModDownloader.Core;

public sealed class InstalledMod
{
    public string Id { get; set; } = "";             // "{modId}:{fileId}", or "bepinex"
    public int ModId { get; set; }
    public int FileId { get; set; }
    public string Name { get; set; } = "";           // mod name
    public string FileName { get; set; } = "";       // Nexus file name (used to match updates)
    public string Version { get; set; } = "";
    public DateTime FileDate { get; set; }
    public DateTime InstalledAt { get; set; }
    public string Location { get; set; } = "";       // where it went, for display
    public bool Disabled { get; set; }
    public List<string> Files { get; set; } = new(); // relative to the game folder, '/' separated
    public List<string> Backups { get; set; } = new();

    // Not saved — filled by update checks.
    [System.Text.Json.Serialization.JsonIgnore] public ModFileInfo? Update { get; set; }
}

public sealed class Manifest
{
    public List<InstalledMod> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Manifest Load(string? path = null)
    {
        path ??= AppPaths.ManifestFile;
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? new(); }
        catch { }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.ManifestFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }

    public InstalledMod? OwnerOf(string rel) =>
        Mods.FirstOrDefault(m => m.Files.Any(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase)));
}

public enum InstallKind
{
    PluginFolder,   // loose DLL mod -> BepInEx/plugins/<Mod Name>/
    BepInExLayout,  // archive already laid out from the game folder (BepInEx/plugins/...)
    BepInExSubdirs, // archive starts at plugins/ patchers/ config/
    GameRoot,       // files meant for the game folder itself
    LoaderPack,     // a BepInEx pack (winhttp.dll / BepInEx/core)
    Unknown         // no DLLs and no recognisable layout: ask the user
}

public sealed class InstallPlan
{
    public InstallKind Kind { get; set; }
    public string SourceFile { get; set; } = "";
    public bool IsArchive { get; set; }
    /// <summary>archive entry key (or "" for a bare file) -> destination relative to the game folder</summary>
    public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);
    public List<string> Skipped { get; } = new();
    public string TargetDescription { get; set; } = "";
}

public static class ModInstaller
{
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };
    private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".url", ".png", ".jpg", ".jpeg", ".gif", ".pdf", ".html", ".htm", ".rtf", ".webp" };
    private static readonly HashSet<string> BepSubdirs = new(StringComparer.OrdinalIgnoreCase) { "plugins", "patchers", "config", "monomod" };
    private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    public static string Norm(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("./")) p = p[2..];
        return p.Trim('/');
    }

    /// <summary>Joins root + relative path and refuses anything that would escape the game folder.</summary>
    public static string SafeJoin(string root, string rel)
    {
        rel = Norm(rel);
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('/').Any(s => s == ".." || s.Contains(':')))
            throw new InvalidOperationException(L.F("Refusing unsafe path in archive: {0}", rel));
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.F("Refusing unsafe path in archive: {0}", rel));
        return full;
    }

    public static string ModFolderName(string modName) => Fmt.SafeFileName(modName);

    /// <summary>Looks inside a downloaded file and decides where everything goes. Writes nothing.</summary>
    public static InstallPlan Analyze(string file, string modName, InstallKind? forceUnknownAs = null)
    {
        var plan = new InstallPlan { SourceFile = file };
        var folder = "BepInEx/plugins/" + ModFolderName(modName);

        // A bare file (some mods upload just the .dll)
        if (!ArchiveExt.Contains(Path.GetExtension(file)))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                plan.Kind = InstallKind.PluginFolder;
                plan.Map[""] = $"{folder}/{name}";
                plan.TargetDescription = folder.Replace('/', '\\');
                return plan;
            }
            throw new InvalidOperationException(L.F("\"{0}\" isn't an archive or a .dll, so it can't be installed automatically.", name));
        }

        plan.IsArchive = true;
        List<string> keys;
        using (var archive = ArchiveFactory.OpenArchive(file, new SharpCompress.Readers.ReaderOptions()))
            keys = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).Select(e => e.Key!).ToList();

        var entries = keys
            .Select(k => (Key: k, Path: Norm(k)))
            .Where(e => e.Path.Length > 0 && !e.Path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) && !Junk.Contains(System.IO.Path.GetFileName(e.Path)))
            .ToList();
        if (entries.Count == 0) throw new InvalidOperationException(L.T("The archive is empty."));

        // Strip wrapper folders like "MyMod-1.2/" that every entry shares.
        var paths = entries.Select(e => e.Path).ToList();
        var stripped = 0;
        while (true)
        {
            var segs = paths.Select(p => p.Split('/')).ToList();
            if (segs.Any(s => s.Length < 2)) break;
            var first = segs[0][0];
            if (!segs.All(s => s[0].Equals(first, StringComparison.OrdinalIgnoreCase))) break;
            if (first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) || BepSubdirs.Contains(first) || first.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) break;
            paths = paths.Select(p => p[(p.IndexOf('/') + 1)..]).ToList();
            stripped++;
        }
        var items = entries.Select((e, i) => (e.Key, Rel: paths[i], Orig: e.Path)).ToList();

        bool IsRootDoc(string rel) => !rel.Contains('/') && DocExt.Contains(System.IO.Path.GetExtension(rel));

        // 1) A BepInEx pack itself
        if (items.Any(i => i.Rel.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)
                        || i.Rel.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)
                        || i.Rel.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)))
        {
            plan.Kind = InstallKind.LoaderPack;
            foreach (var i in items) plan.Map[i.Key] = i.Rel;
            plan.TargetDescription = L.T("the game folder (this is a BepInEx pack)");
            return plan;
        }

        // 2) Laid out from the game folder: .../BepInEx/plugins/...
        var withBep = items.Select(i => (i.Key, i.Rel, Idx: Array.FindIndex(i.Rel.Split('/'), s => s.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))))
                           .Where(x => x.Idx >= 0).ToList();
        if (withBep.Count > 0)
        {
            var depth = withBep.Min(x => x.Idx);
            var prefixSegs = withBep.First(x => x.Idx == depth).Rel.Split('/').Take(depth).ToArray();
            var prefix = prefixSegs.Length == 0 ? "" : string.Join('/', prefixSegs) + "/";
            plan.Kind = InstallKind.BepInExLayout;
            foreach (var i in items)
            {
                if (!i.Rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { plan.Skipped.Add(i.Rel); continue; }
                var rel = i.Rel[prefix.Length..];
                if (IsRootDoc(rel)) { plan.Skipped.Add(i.Rel); continue; }
                plan.Map[i.Key] = rel;
            }
            plan.TargetDescription = L.T("the game folder, using the mod's own BepInEx layout");
            return plan;
        }

        // 3) Starts at plugins/ patchers/ config/
        if (items.Any(i => i.Rel.Contains('/') && BepSubdirs.Contains(i.Rel.Split('/')[0])))
        {
            plan.Kind = InstallKind.BepInExSubdirs;
            foreach (var i in items)
            {
                var first = i.Rel.Split('/')[0];
                if (i.Rel.Contains('/') && BepSubdirs.Contains(first)) plan.Map[i.Key] = "BepInEx/" + i.Rel;
                else if (IsRootDoc(i.Rel)) plan.Skipped.Add(i.Rel);
                else plan.Map[i.Key] = $"{folder}/{i.Rel}";
            }
            plan.TargetDescription = L.T("BepInEx\\plugins (and the folders the mod includes)");
            return plan;
        }

        // 4) Loose DLL mod
        var kind = items.Any(i => i.Rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? InstallKind.PluginFolder : (forceUnknownAs ?? InstallKind.Unknown);
        plan.Kind = kind;
        switch (kind)
        {
            case InstallKind.PluginFolder:
                foreach (var i in items) plan.Map[i.Key] = $"{folder}/{i.Rel}";
                plan.TargetDescription = folder.Replace('/', '\\');
                break;
            case InstallKind.GameRoot:
                // Keep the archive's own folders here: "Textures/x.png" must stay in Textures.
                foreach (var i in items)
                    if (IsRootDoc(i.Orig)) plan.Skipped.Add(i.Orig); else plan.Map[i.Key] = i.Orig;
                plan.TargetDescription = L.T("the game folder");
                break;
            default:
                plan.TargetDescription = L.T("unknown — the archive has no plugin DLLs");
                foreach (var i in items) plan.Map[i.Key] = $"{folder}/{i.Rel}";
                break;
        }
        return plan;
    }

    /// <summary>Copies the planned files into the game folder, backing up anything it replaces. Rolls back on failure.</summary>
    public static InstalledMod Install(InstallPlan plan, GameInstall game, Manifest manifest, InstalledMod meta, string? manifestPath = null, bool backupExisting = true)
    {
        if (game.BlocksInstall()) throw new InvalidOperationException(L.T("Close Nomad Drive before installing mods."));
        var live = game.IsRunning(); // with the in-game Mods menu: files in use are staged and swapped in at next launch
        if (plan.Map.Count == 0) throw new InvalidOperationException(L.T("Nothing in this download can be installed."));

        var backupRoot = Path.Combine(AppPaths.Backups, Fmt.SafeFileName(meta.Id.Replace(':', '_')));
        var written = new List<string>();
        var backedUp = new List<string>();
        var dests = plan.Map.ToDictionary(kv => kv.Key, kv => (Rel: Norm(kv.Value), Full: SafeJoin(game.Root, kv.Value)));

        try
        {
            // Back up existing files first.
            foreach (var (rel, full) in dests.Values)
            {
                if (!File.Exists(full)) continue;
                var owner = manifest.OwnerOf(rel);
                if (owner?.Id == meta.Id) continue; // reinstalling over our own files
                if (!backupExisting) { if (owner != null) owner.Files.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase)); continue; }
                var bak = SafeJoin(backupRoot, rel);
                if (!File.Exists(bak))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Copy(full, bak, true);
                }
                backedUp.Add(rel);
                // Another tracked mod owned it: hand ownership over so its uninstall won't delete ours.
                if (owner != null) owner.Files.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
            }

            if (!plan.IsArchive)
            {
                var single = dests[""];
                Directory.CreateDirectory(Path.GetDirectoryName(single.Full)!);
                File.Copy(plan.SourceFile, Target(single.Full, live), true);
                written.Add(single.Rel);
            }
            else
            {
                using var archive = ArchiveFactory.OpenArchive(plan.SourceFile, new SharpCompress.Readers.ReaderOptions());
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    // Solid archives must be read front to back.
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        var e = reader.Entry;
                        if (e.IsDirectory || e.Key == null || !dests.TryGetValue(e.Key, out var d)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(d.Full)!);
                        using (var fs = File.Create(Target(d.Full, live))) reader.WriteEntryTo(fs);
                        written.Add(d.Rel);
                    }
                }
                else
                {
                    foreach (var e in archive.Entries)
                    {
                        if (e.IsDirectory || e.Key == null || !dests.TryGetValue(e.Key, out var d)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(d.Full)!);
                        using (var src = e.OpenEntryStream())
                        using (var fs = File.Create(Target(d.Full, live))) src.CopyTo(fs);
                        written.Add(d.Rel);
                    }
                }
                var missing = dests.Values.Select(v => v.Rel).Except(written, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0) throw new InvalidOperationException(L.F("{0} file(s) could not be extracted from the archive.", missing.Count));
            }

            meta.Files = written.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Reinstalling the same file keeps the backups from the first install, so uninstall still restores the originals.
            var previous = manifest.Mods.FirstOrDefault(m => m.Id == meta.Id && !ReferenceEquals(m, meta));
            meta.Backups = backedUp.Concat(previous?.Backups ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            meta.InstalledAt = DateTime.Now;
            meta.Location = plan.TargetDescription;
            meta.Disabled = false;
            manifest.Mods.RemoveAll(m => m.Id == meta.Id);
            manifest.Mods.Add(meta);
            manifest.Save(manifestPath);
            return meta;
        }
        catch
        {
            // Roll back: remove what we wrote, put back what we replaced.
            foreach (var rel in written) { TryDelete(SafeJoin(game.Root, rel) + Pending.NewSuffix); if (!live) TryDelete(SafeJoin(game.Root, rel)); }
            foreach (var rel in backedUp)
            {
                var bak = SafeJoin(backupRoot, rel);
                if (File.Exists(bak)) { try { File.Copy(bak, SafeJoin(game.Root, rel), true); } catch { } }
            }
            CleanEmptyDirs(game, written);
            throw;
        }
    }

    /// <summary>
    /// Where a file goes. While the game runs (with the in-game Mods menu), a file that already exists may be in use,
    /// so the new copy is written next to it as *.nmm-new and swapped in when the game next starts.
    /// </summary>
    private static string Target(string full, bool live) =>
        live && (File.Exists(full) || File.Exists(full + ".disabled")) ? full + Pending.NewSuffix : full;

    public static void Uninstall(InstalledMod mod, GameInstall game, Manifest manifest, string? manifestPath = null)
    {
        if (game.BlocksInstall()) throw new InvalidOperationException(L.T("Close Nomad Drive before removing mods."));
        var live = game.IsRunning();
        foreach (var rel in mod.Files)
        {
            var full = SafeJoin(game.Root, rel);
            if (!TryDelete(full) && live) Pending.Add(game.Root, "remove", rel); // in use: removed at next launch
            TryDelete(full + ".disabled");
        }
        var backupRoot = Path.Combine(AppPaths.Backups, Fmt.SafeFileName(mod.Id.Replace(':', '_')));
        foreach (var rel in mod.Backups)
        {
            var bak = SafeJoin(backupRoot, rel);
            if (!File.Exists(bak)) continue;
            var dest = SafeJoin(game.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(bak, live && File.Exists(dest) ? dest + Pending.NewSuffix : dest, true);
        }
        try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true); } catch { }
        CleanEmptyDirs(game, mod.Files);
        manifest.Mods.Remove(mod);
        manifest.Save(manifestPath);
    }

    /// <summary>BepInEx only loads *.dll, so disabling renames them to *.dll.disabled.</summary>
    public static void SetDisabled(InstalledMod mod, bool disabled, GameInstall game, Manifest manifest, string? manifestPath = null)
    {
        if (game.IsRunning()) throw new InvalidOperationException(L.T("Close Nomad Drive first."));
        foreach (var rel in mod.Files.Where(IsLoadableDll))
        {
            var on = SafeJoin(game.Root, rel);
            var off = on + ".disabled";
            if (disabled && File.Exists(on)) File.Move(on, off, true);
            if (!disabled && File.Exists(off)) File.Move(off, on, true);
        }
        mod.Disabled = disabled;
        manifest.Save(manifestPath);
    }

    public static bool IsLoadableDll(string rel) =>
        rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        (rel.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("BepInEx/patchers/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Plugin folders/DLLs in BepInEx\plugins that this app didn't install (e.g. your own mods).</summary>
    public static List<(string Rel, bool Disabled)> FindUntracked(GameInstall game, Manifest manifest)
    {
        var result = new List<(string, bool)>();
        if (!Directory.Exists(game.PluginsDir)) return result;
        var owned = manifest.Mods.SelectMany(m => m.Files).Select(f => f.ToLowerInvariant()).ToHashSet();

        foreach (var entry in Directory.EnumerateFileSystemEntries(game.PluginsDir))
        {
            var rel = "BepInEx/plugins/" + Path.GetFileName(entry);
            var lower = rel.ToLowerInvariant();
            if (owned.Contains(lower) || owned.Contains(lower.Replace(".disabled", ""))) continue;
            if (Directory.Exists(entry))
            {
                if (owned.Any(o => o.StartsWith(lower + "/"))) continue;
                var dlls = Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)).ToList();
                if (dlls.Count == 0) continue;
                result.Add((rel, dlls.All(f => f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))));
            }
            else if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) result.Add((rel, false));
            else if (entry.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)) result.Add((rel[..^".disabled".Length], true));
        }
        return result;
    }

    public static void SetUntrackedDisabled(GameInstall game, string rel, bool disabled)
    {
        if (game.IsRunning()) throw new InvalidOperationException(L.T("Close Nomad Drive first."));
        var full = SafeJoin(game.Root, rel);
        IEnumerable<string> dlls = Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).ToList()
            : new[] { full, full + ".disabled" };
        foreach (var f in dlls)
        {
            if (disabled && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(f)) File.Move(f, f + ".disabled", true);
            if (!disabled && f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase) && File.Exists(f)) File.Move(f, f[..^".disabled".Length], true);
        }
    }

    /// <summary>Deletes the file if it's there. False only when it exists and couldn't be deleted (e.g. in use).</summary>
    private static bool TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); return true; } catch { return false; } }

    /// <summary>
    /// Brings the list in line with the disk: picks up enable/disable done in the game's Mods menu and
    /// forgets mods whose files are all gone (removed in the game).
    /// </summary>
    public static bool Reconcile(Manifest manifest, GameInstall game)
    {
        var changed = false;
        foreach (var m in manifest.Mods.ToList())
        {
            if (m.Files.Count == 0 || m.Id == "bepinex") continue;
            var fulls = m.Files.Select(r => { try { return SafeJoin(game.Root, r); } catch { return null; } }).Where(f => f != null).Cast<string>().ToList();
            bool Any(string f) => File.Exists(f) || File.Exists(f + ".disabled") || File.Exists(f + Pending.NewSuffix);
            if (fulls.Count > 0 && !fulls.Any(Any))
            {
                manifest.Mods.Remove(m); changed = true; continue;
            }
            var dlls = m.Files.Where(IsLoadableDll).Select(r => SafeJoin(game.Root, r)).ToList();
            if (dlls.Count == 0) continue;
            var off = dlls.All(f => !File.Exists(f) && File.Exists(f + ".disabled"));
            if (off != m.Disabled) { m.Disabled = off; changed = true; }
        }
        return changed;
    }

    private static void CleanEmptyDirs(GameInstall game, IEnumerable<string> rels)
    {
        var keep = new[] { game.Root, game.BepInExDir, game.PluginsDir, Path.Combine(game.BepInExDir, "patchers"), Path.Combine(game.BepInExDir, "config"), Path.Combine(game.BepInExDir, "core") }
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirs = rels.Select(r => Path.GetDirectoryName(SafeJoin(game.Root, r))!).Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(d => d.Length).ToList();
        foreach (var start in dirs)
        {
            var d = start;
            while (!string.IsNullOrEmpty(d) && !keep.Contains(d.TrimEnd(Path.DirectorySeparatorChar)) && d.StartsWith(game.Root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                    else break;
                }
                catch { break; }
                d = Path.GetDirectoryName(d);
            }
        }
    }
}
