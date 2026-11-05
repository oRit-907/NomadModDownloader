using System.IO.Compression;

namespace NomadModDownloader.Core;

public sealed record SaveBackup(string Path, DateTime Time, string Reason, long Size);

/// <summary>
/// Backs up Nomad Drive's saves (Unity keeps them in %USERPROFILE%\AppData\LocalLow\Indie Devil\Nomad Drive)
/// to zip files in the app's data folder, and puts them back on request.
/// </summary>
public static class SaveBackups
{
    public const string Company = "Indie Devil";
    public const string Product = "Nomad Drive";
    public const int KeepAutomatic = 10;

    public static string BackupDir => AppPaths.Ensure(System.IO.Path.Combine(AppPaths.Root, "save-backups"));

    /// <summary>Override for tests.</summary>
    public static string? SaveFolderOverride { get; set; }

    public static string? FindSaveFolder()
    {
        if (SaveFolderOverride != null) return Directory.Exists(SaveFolderOverride) ? SaveFolderOverride : null;
        try
        {
            var localLow = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            var dir = System.IO.Path.Combine(localLow, Company, Product);
            if (Directory.Exists(dir)) return dir;
            if (Directory.Exists(localLow))
                foreach (var company in Directory.EnumerateDirectories(localLow))
                {
                    var d = System.IO.Path.Combine(company, Product);
                    if (Directory.Exists(d)) return d;
                }
        }
        catch { }
        return null;
    }

    /// <summary>Logs, crash dumps and Unity's caches aren't saves.</summary>
    private static bool Skip(string rel)
    {
        var name = System.IO.Path.GetFileName(rel);
        var top = rel.Split('/', '\\')[0];
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
            || top.Equals("Unity", StringComparison.OrdinalIgnoreCase) || top.Equals("Crashes", StringComparison.OrdinalIgnoreCase)
            || top.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="reason">Shown in the list, e.g. "before installing X". Automatic ones are pruned to the newest <see cref="KeepAutomatic"/>.</param>
    public static SaveBackup? Create(string reason, bool automatic)
    {
        var src = FindSaveFolder();
        if (src == null) return null;
        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Select(f => (full: f, rel: System.IO.Path.GetRelativePath(src, f).Replace('\\', '/')))
            .Where(x => !Skip(x.rel)).ToList();
        if (files.Count == 0) return null;

        var stamp = DateTime.Now;
        var name = $"{stamp:yyyy-MM-dd_HH-mm-ss}{(automatic ? "_auto" : "")}.zip";
        var path = System.IO.Path.Combine(BackupDir, name);
        for (var n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(BackupDir, $"{stamp:yyyy-MM-dd_HH-mm-ss}_{n}{(automatic ? "_auto" : "")}.zip");
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (full, rel) in files)
            {
                try
                {
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(full);
                    using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
                catch (IOException) { /* a file in use by the game: skip it */ }
            }
            var note = zip.CreateEntry(".backup-reason.txt");
            using (var w = new StreamWriter(note.Open())) w.Write(reason);
        }
        File.Move(tmp, path);
        if (automatic) Prune();
        return Describe(path);
    }

    public static List<SaveBackup> List()
    {
        var list = new List<SaveBackup>();
        foreach (var f in Directory.EnumerateFiles(BackupDir, "*.zip"))
            if (Describe(f) is { } b) list.Add(b);
        return list.OrderByDescending(b => b.Time).ToList();
    }

    private static SaveBackup? Describe(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var reason = "";
            if (zip.GetEntry(".backup-reason.txt") is { } e) { using var r = new StreamReader(e.Open()); reason = r.ReadToEnd(); }
            return new SaveBackup(path, File.GetCreationTime(path) is var c && c.Year > 2000 ? c : File.GetLastWriteTime(path), reason, new FileInfo(path).Length);
        }
        catch { return null; }
    }

    private static void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(BackupDir, "*_auto.zip").OrderByDescending(f => f).Skip(KeepAutomatic))
            try { File.Delete(old); } catch { }
    }

    /// <summary>Puts a backup back. The current saves are backed up first, so a restore can be undone.</summary>
    public static SaveBackup? Restore(SaveBackup backup)
    {
        var dest = FindSaveFolder() ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", Company, Product);
        Directory.CreateDirectory(dest);
        var undo = Create(L.T("Before restoring a backup"), automatic: false);

        using var zip = ZipFile.OpenRead(backup.Path);
        var entries = zip.Entries.Where(e => e.FullName != ".backup-reason.txt" && !e.FullName.EndsWith('/')).ToList();
        // Remove current save files (not logs or caches) so files the backup doesn't have don't linger.
        foreach (var f in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).ToList())
        {
            var rel = System.IO.Path.GetRelativePath(dest, f).Replace('\\', '/');
            if (!Skip(rel)) File.Delete(f);
        }
        foreach (var e in entries)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dest, e.FullName));
            if (!target.StartsWith(System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue; // never write outside the save folder
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
        return undo;
    }

    public static void Delete(SaveBackup backup) { try { File.Delete(backup.Path); } catch { } }
}
