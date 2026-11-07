using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace NomadModDownloader.Core;

public enum BepInExState { NotInstalled, NeedsFirstRun, Ready, Disabled, Broken, Il2CppGame }

/// <param name="Major">5 or 6 when installed, 0 otherwise.</param>
public sealed record BepInExStatus(BepInExState State, string? Version, string Message, int Major = 0);

public sealed record BepInExPlan(string Version, Uri Url, long SizeBytes, string Architecture, string GameRoot);

public static class BepInExManager
{
    // Fallback if the GitHub API can't be reached (rate limit, offline API).
    private const string PinnedVersion = "5.4.23.5";

    /// <summary>
    /// The BepInEx the app installs: BepInEx 6 for Unity Mono, pinned to a known build (BepInEx 6 is still a
    /// pre-release, so "latest" could be anything). BepInEx 5 mods keep working through the BepInEx 5
    /// compatibility layer (<see cref="BepInExCompat"/>) that's set up with it.
    /// </summary>
    public const string Bep6Version = "6.0.0-pre.2";

    /// <summary>5 or 6 from the files in BepInEx\core, 0 if neither is there.</summary>
    public static int MajorOf(GameInstall game)
    {
        var core = Path.Combine(game.BepInExDir, "core");
        if (File.Exists(Path.Combine(core, "BepInEx.Unity.Mono.dll")) || File.Exists(Path.Combine(core, "BepInEx.Unity.Mono.Preloader.dll"))) return 6;
        if (File.Exists(Path.Combine(core, "BepInEx.dll"))) return 5;
        if (File.Exists(Path.Combine(core, "BepInEx.Core.dll"))) return 6;
        return 0;
    }

    /// <summary>BepInEx 6 for Mono games: what the app installs.</summary>
    public static BepInExPlan Plan6(GameInstall game)
    {
        var arch = game.Is64Bit ? "x64" : "x86";
        return new BepInExPlan(Bep6Version,
            new Uri($"https://github.com/BepInEx/BepInEx/releases/download/v{Bep6Version}/BepInEx-Unity.Mono-win-{arch}-{Bep6Version}.zip"), 0, arch, game.Root);
    }

    public static BepInExStatus Check(GameInstall game)
    {
        var s = CheckRaw(game);
        return s with { Major = s.State == BepInExState.NotInstalled ? 0 : MajorOf(game) };
    }

    private static BepInExStatus CheckRaw(GameInstall game)
    {
        var major = MajorOf(game);
        var core = Path.Combine(game.BepInExDir, "core", major == 6 ? "BepInEx.Core.dll" : "BepInEx.dll");
        var proxy = Path.Combine(game.Root, "winhttp.dll");
        var hasCore = File.Exists(core);
        var hasProxy = File.Exists(proxy);

        if (!hasCore && !hasProxy)
        {
            if (game.IsIl2Cpp)
                return new(BepInExState.Il2CppGame, null, L.T("This copy of the game is IL2CPP, which needs BepInEx 6 — automatic install only supports the normal (Mono) build."));
            return new(BepInExState.NotInstalled, null, L.T("BepInEx is not installed."));
        }

        string? version = null;
        try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(core).ProductVersion?.Split('+')[0]; } catch { }

        if (hasCore != hasProxy)
            return new(BepInExState.Broken, version, hasCore ? L.T("BepInEx files are there but winhttp.dll is missing, so it won't load.") : L.T("winhttp.dll is there but the BepInEx folder is missing."));

        var doorstop = Path.Combine(game.Root, "doorstop_config.ini");
        if (File.Exists(doorstop) && Regex.IsMatch(File.ReadAllText(doorstop), @"(?im)^\s*enabled\s*=\s*false"))
            return new(BepInExState.Disabled, version, L.T("BepInEx is installed but switched off in doorstop_config.ini."));

        var ranOnce = File.Exists(Path.Combine(game.BepInExDir, "config", "BepInEx.cfg")) || File.Exists(Path.Combine(game.BepInExDir, "LogOutput.log"));
        if (!ranOnce)
            return new(BepInExState.NeedsFirstRun, version, L.T("BepInEx is installed. Launch the game once so it finishes setting itself up."));

        return new(BepInExState.Ready, version, L.T("BepInEx is installed and ready."));
    }

    /// <summary>What a new install gets: BepInEx 6 (with BepInEx 5 support). Nothing is written to disk.</summary>
    public static Task<BepInExPlan> PlanAsync(HttpClient http, GameInstall game, CancellationToken ct) => Task.FromResult(Plan6(game));

    /// <summary>Works out which BepInEx 5 build to download (for switching back). Nothing is written to disk.</summary>
    public static async Task<BepInExPlan> Plan5Async(HttpClient http, GameInstall game, CancellationToken ct)
    {
        var arch = game.Is64Bit ? "x64" : "x86";
        return await LatestAsync(http, game, ct) ?? new BepInExPlan(PinnedVersion,
            new Uri($"https://github.com/BepInEx/BepInEx/releases/download/v{PinnedVersion}/BepInEx_win_{arch}_{PinnedVersion}.zip"),
            0, arch, game.Root);
    }

    /// <summary>The newest stable BepInEx 5 on GitHub, or null if GitHub can't be reached right now.</summary>
    public static async Task<BepInExPlan?> LatestAsync(HttpClient http, GameInstall game, CancellationToken ct)
    {
        var arch = game.Is64Bit ? "x64" : "x86";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=20");
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var releases = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsArray();
                foreach (var rel in releases)
                {
                    if (rel == null || rel["prerelease"]?.GetValue<bool>() == true || rel["draft"]?.GetValue<bool>() == true) continue;
                    var tag = rel["tag_name"]?.GetValue<string>()?.TrimStart('v') ?? "";
                    if (!tag.StartsWith("5.")) continue;
                    foreach (var a in rel["assets"]?.AsArray() ?? new JsonArray())
                    {
                        var name = a?["name"]?.GetValue<string>() ?? "";
                        if (Regex.IsMatch(name, $@"^BepInEx_win_{arch}_5[\d.]+\.zip$", RegexOptions.IgnoreCase))
                            return new BepInExPlan(tag, new Uri(a!["browser_download_url"]!.GetValue<string>()), a["size"]?.GetValue<long>() ?? 0, arch, game.Root);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    /// <summary>A newer BepInEx 5 than the one in the game, or null (up to date, not installed, or GitHub unreachable).</summary>
    public static async Task<BepInExPlan?> CheckForUpdateAsync(HttpClient http, GameInstall game, CancellationToken ct)
    {
        var s = Check(game);
        if (s.State is not (BepInExState.Ready or BepInExState.NeedsFirstRun or BepInExState.Disabled) || s.Version == null) return null;
        if (s.Major != 5) return null; // BepInEx 6 is pinned to a tested build; it's never "updated" to BepInEx 5
        if (!Version.TryParse(s.Version, out var have)) return null;
        var latest = await LatestAsync(http, game, ct);
        if (latest == null || !Version.TryParse(latest.Version, out var newest)) return null;
        return Normalize(newest) > Normalize(have) ? latest : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// Updates BepInEx in place. Plugins, configs and doorstop_config.ini are kept (the Nomad Drive pack points
    /// doorstop at the unstripped DLLs the game needs). The old core files are backed up and put back if anything fails.
    /// </summary>
    public static async Task<List<string>> UpdateAsync(HttpClient http, GameInstall game, BepInExPlan plan, IProgress<double>? progress, CancellationToken ct)
    {
        if (game.IsRunning()) throw new InvalidOperationException(L.T("Close Nomad Drive before installing BepInEx."));
        if (MajorOf(game) == 6) throw new InvalidOperationException(L.T("BepInEx 6 is installed. Use Check BepInEx to switch back to BepInEx 5."));
        var old = Check(game).Version ?? "old";
        var backup = Path.Combine(AppPaths.Backups, "bepinex-" + old);
        void Copy(string from, string to)
        {
            foreach (var f in Directory.Exists(from) ? Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
            }
        }
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        Copy(Path.Combine(game.BepInExDir, "core"), Path.Combine(backup, "BepInEx", "core"));
        foreach (var f in new[] { "winhttp.dll", ".doorstop_version" })
            if (File.Exists(Path.Combine(game.Root, f))) { Directory.CreateDirectory(backup); File.Copy(Path.Combine(game.Root, f), Path.Combine(backup, f), true); }

        try
        {
            return await InstallAsync(http, game, plan, progress, ct, keep: new[] { "doorstop_config.ini" });
        }
        catch
        {
            // Put the previous version back.
            Copy(Path.Combine(backup, "BepInEx", "core"), Path.Combine(game.BepInExDir, "core"));
            foreach (var f in new[] { "winhttp.dll", ".doorstop_version" })
                if (File.Exists(Path.Combine(backup, f))) File.Copy(Path.Combine(backup, f), Path.Combine(game.Root, f), true);
            throw;
        }
    }

    /// <summary>Downloads and unpacks BepInEx into the game folder. Only call after the user approved the plan.</summary>
    public static async Task<List<string>> InstallAsync(HttpClient http, GameInstall game, BepInExPlan plan, IProgress<double>? progress, CancellationToken ct, string[]? keep = null)
    {
        if (game.IsRunning()) throw new InvalidOperationException(L.T("Close Nomad Drive before installing BepInEx."));

        var zip = await NexusApi.DownloadFileAsync(http, plan.Url, AppPaths.Downloads, $"BepInEx_win_{plan.Architecture}_{plan.Version}", progress, ct);

        var written = new List<string>();
        using (var archive = ArchiveFactory.OpenArchive(zip, new SharpCompress.Readers.ReaderOptions()))
        {
            // Sanity check: this must really be a BepInEx pack.
            var keys = archive.Entries.Where(e => !e.IsDirectory).Select(e => ModInstaller.Norm(e.Key ?? "")).ToList();
            if (!keys.Any(k => k.EndsWith("BepInEx/core/BepInEx.dll", StringComparison.OrdinalIgnoreCase) || k.EndsWith("BepInEx/core/BepInEx.Core.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L.T("The downloaded file doesn't look like BepInEx. Nothing was installed."));

            foreach (var e in archive.Entries.Where(e => !e.IsDirectory))
            {
                var rel = ModInstaller.Norm(e.Key ?? "");
                var dest = ModInstaller.SafeJoin(game.Root, rel);
                if (keep != null && File.Exists(dest) && keep.Any(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase))) continue; // leave the user's copy
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var src = e.OpenEntryStream())
                using (var fs = File.Create(dest)) src.CopyTo(fs);
                written.Add(rel);
            }
        }

        Directory.CreateDirectory(game.PluginsDir);
        // Nomad Drive's mscorlib is stripped: without the full Mono libraries BepInEx's preloader crashes.
        if (BepInExChecker.EnsureNomadSetup(game)) written.Add(BepInExChecker.DotNetFixFolder + "/");
        // Neither BepInEx loads the other's mods by itself: add the compatibility layer for the other version.
        if (MajorOf(game) == 6) { written.AddRange(BepInExCompat.Install(game)); BepInEx6Support.Remove(game); }
        else { written.AddRange(BepInEx6Support.Install(game)); BepInExCompat.Remove(game); }
        // Restore the Unity code the game's build stripped (the patcher build for this BepInEx). A failed download
        // doesn't fail the install; Check BepInEx offers it again.
        try { written.AddRange(await UnityCompat.InstallAsync(http, game, null, ct)); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return written;
    }

    /// <summary>
    /// Reinstalls BepInEx 5 from scratch for a broken or wrong install: the old core folder, winhttp.dll and
    /// doorstop_config.ini go to a backup first. Plugins, patchers and settings are kept.
    /// </summary>
    public static async Task<(List<string> Written, string Backup)> RepairAsync(HttpClient http, GameInstall game, BepInExPlan plan, IProgress<double>? progress, CancellationToken ct)
    {
        if (game.IsRunning()) throw new InvalidOperationException(L.T("Close Nomad Drive before installing BepInEx."));
        var backup = Path.Combine(AppPaths.Backups, $"bepinex-repair-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        var core = Path.Combine(game.BepInExDir, "core");
        if (Directory.Exists(core))
        {
            foreach (var f in Directory.EnumerateFiles(core, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(backup, "BepInEx", "core", Path.GetRelativePath(core, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
            }
            Directory.Delete(core, true);
        }
        foreach (var f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" })
        {
            var p = Path.Combine(game.Root, f);
            if (File.Exists(p)) { File.Copy(p, Path.Combine(backup, f), true); File.Delete(p); }
        }
        var written = await InstallAsync(http, game, plan, progress, ct);
        return (written, backup);
    }
}
