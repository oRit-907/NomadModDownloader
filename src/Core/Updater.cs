using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NomadModDownloader.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string AssetApiUrl, long Size, string PageUrl);

public enum UpdateCheck { UpToDate, Available, Failed }

/// <summary>
/// Self-update from the public GitHub Releases of oRit-907/NomadModDownloader. No account or token
/// is needed. A token saved by an older build is still sent, and dropped if GitHub rejects it.
/// </summary>
public sealed class Updater
{
    public const string Repo = "oRit-907/NomadModDownloader";
    public const string ExeName = "NomadModDownloader.exe";
    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private readonly HttpClient _http;
    public string? Token { get; set; }

    public Updater(HttpClient http) { _http = http; }

    /// <summary>Sends with the saved token if there is one; if GitHub rejects it, tries again without (the repo is public).</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, string accept, HttpCompletionOption mode, CancellationToken ct)
    {
        using (var req = Request(url, accept, useToken: true))
        {
            var resp = await _http.SendAsync(req, mode, ct);
            if (string.IsNullOrWhiteSpace(Token) || resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Forbidden))
                return resp;
            resp.Dispose();
        }
        using var anon = Request(url, accept, useToken: false);
        return await _http.SendAsync(anon, mode, ct);
    }

    private HttpRequestMessage Request(string url, string accept, bool useToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (useToken && !string.IsNullOrWhiteSpace(Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token.Trim());
        return req;
    }

    public async Task<(UpdateCheck Result, UpdateInfo? Info, string? Error)> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync($"https://api.github.com/repos/{Repo}/releases/latest", "application/vnd.github+json", HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return (UpdateCheck.UpToDate, null, null); // no releases published (yet)
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return (UpdateCheck.Failed, null, L.T("GitHub is busy right now. Try again in a while."));
            if (!resp.IsSuccessStatusCode) return (UpdateCheck.Failed, null, $"GitHub ({(int)resp.StatusCode})");

            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
            var tag = j["tag_name"]?.GetValue<string>() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return (UpdateCheck.Failed, null, "Bad release tag: " + tag);
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            var asset = j["assets"]?.AsArray().FirstOrDefault(a => (a?["name"]?.GetValue<string>() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset == null) return (UpdateCheck.Failed, null, "The latest release has no .exe attached.");
            var info = new UpdateInfo(v, tag, j["body"]?.GetValue<string>() ?? "", asset["url"]!.GetValue<string>(),
                asset["size"]?.GetValue<long>() ?? 0, j["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repo}/releases");
            return (v > Current ? UpdateCheck.Available : UpdateCheck.UpToDate, info, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (UpdateCheck.Failed, null, ex.Message); }
    }

    /// <summary>Downloads the new exe next to the app's data (not over the running one).</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        // The asset's API URL redirects to a signed download link (GitHub drops the token on that hop).
        using var resp = await SendAsync(info.AssetApiUrl, "application/octet-stream", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var dir = Path.Combine(AppPaths.Root, "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "NomadModDownloader.new.exe");
        var total = resp.Content.Headers.ContentLength ?? info.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = File.Create(path))
        {
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        // Sanity check: a Windows program, roughly the expected size.
        var head = new byte[2];
        await using (var check = File.OpenRead(path)) await check.ReadExactlyAsync(head, ct);
        if (head[0] != 'M' || head[1] != 'Z') { File.Delete(path); throw new InvalidOperationException(L.T("The downloaded update isn't a valid program. Nothing was changed.")); }
        if (info.Size > 0 && new FileInfo(path).Length != info.Size) { File.Delete(path); throw new InvalidOperationException(L.T("The update download was incomplete. Nothing was changed.")); }
        return path;
    }

    /// <summary>
    /// Starts a helper that swaps the exe once this process has exited, then starts the new version. The old exe is kept
    /// as NomadModDownloader.old.exe until the next update.
    /// </summary>
    /// <summary>
    /// After an update: the new version has started fine, so remove the previous exe (kept as "&lt;name&gt;.old.exe"
    /// until now, so a failed swap could be undone) and the downloaded installer copy. Retries for a few seconds
    /// while the update script finishes.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var current = Environment.ProcessPath;
                if (current == null) return;
                var dir = Path.GetDirectoryName(current)!;
                var targets = new List<string> { Path.Combine(dir, Path.GetFileNameWithoutExtension(current) + ".old.exe") };
                var updateDir = Path.Combine(AppPaths.Root, "update");
                if (Directory.Exists(updateDir)) targets.AddRange(Directory.EnumerateFiles(updateDir, "*.new.exe"));
                for (var attempt = 0; attempt < 10 && targets.Count > 0; attempt++)
                {
                    foreach (var f in targets.ToList())
                    {
                        try { if (File.Exists(f)) File.Delete(f); targets.Remove(f); }
                        catch { /* still in use: try again shortly */ }
                    }
                    if (targets.Count > 0) await Task.Delay(1000);
                }
            }
            catch { }
        });
    }

    public static void ApplyAndRestart(string newExe)
    {
        var current = Environment.ProcessPath!;
        var old = Path.Combine(Path.GetDirectoryName(current)!, Path.GetFileNameWithoutExtension(current) + ".old.exe");
        var script = Path.Combine(AppPaths.Root, "update", "apply-update.cmd");
        File.WriteAllText(script,
$@"@echo off
setlocal
set PID={Environment.ProcessId}
:wait
tasklist /FI ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul && (timeout /t 1 /nobreak >nul & goto wait)
del /f /q ""{old}"" >nul 2>&1
move /y ""{current}"" ""{old}"" >nul
move /y ""{newExe}"" ""{current}"" >nul
if errorlevel 1 move /y ""{old}"" ""{current}"" >nul
start """" ""{current}""
del ""%~f0""
");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        // The caller closes the app; the script waits for that, swaps the exe and starts it again.
    }
}
