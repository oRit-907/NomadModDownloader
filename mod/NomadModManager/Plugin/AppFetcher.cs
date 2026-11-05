using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    /// <summary>
    /// Gets Nomad Mod Downloader from its GitHub releases (it isn't on Nexus): finds the latest release's exe,
    /// downloads it with progress, checks the size against GitHub's, puts it in
    /// %LOCALAPPDATA%\Programs\NomadModDownloader, registers it for the game and opens it.
    /// Only ever runs when the player presses the button.
    /// </summary>
    internal static class AppFetcher
    {
        public const string Repo = "oRit-907/NomadModDownloader";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";

        public static bool Busy { get; private set; }
        public static float Progress { get; private set; }
        public static string Status { get; private set; } = "";
        public static long Size { get; private set; }

        public static string InstallDir => Path.Combine(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Paths.GameRootPath, "Programs"), "NomadModDownloader");

        public static void Start(MonoBehaviour host, Action<bool, string> done)
        {
            if (Busy) return;
            host.StartCoroutine(Run(done));
        }

        private static IEnumerator Run(Action<bool, string> done)
        {
            Busy = true; Progress = 0; Status = "Finding the latest version…";
            string url = null, version = null, assetName = null;
            long size = 0;

            using (var req = UnityWebRequest.Get(Api))
            {
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                req.timeout = 30;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Finish(done, false, "Couldn't reach GitHub (" + req.error + ")."); yield break; }
                try
                {
                    var rel = JObject.Parse(req.downloadHandler.text);
                    version = ((string)rel["tag_name"] ?? "").TrimStart('v', 'V');
                    var asset = (rel["assets"] as JArray ?? new JArray()).OfType<JObject>()
                        .FirstOrDefault(a => ((string)a["name"] ?? "").StartsWith("NomadModDownloader", StringComparison.OrdinalIgnoreCase)
                                          && ((string)a["name"] ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (asset != null)
                    {
                        url = (string)asset["browser_download_url"];
                        size = (long?)asset["size"] ?? 0;
                        assetName = (string)asset["name"];
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                Finish(done, false, "The latest release has no app to download.");
                yield break;
            }

            Size = size;
            Directory.CreateDirectory(InstallDir);
            var target = Path.Combine(InstallDir, "NomadModDownloader.exe");
            var temp = target + ".download";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            Status = $"Downloading Nomad Mod Downloader {version}…";
            using (var req = new UnityWebRequest(url, "GET"))
            {
                req.downloadHandler = new DownloadHandlerFile(temp, false);
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                var op = req.SendWebRequest();
                // (downloadProgress is stripped from the game; downloadedBytes against GitHub's size works the same)
                while (!op.isDone) { Progress = size > 0 ? Mathf.Clamp01(req.downloadedBytes / (float)size) : 0f; yield return null; }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    Finish(done, false, "Download failed (" + req.error + ").");
                    yield break;
                }
            }

            try
            {
                var got = new FileInfo(temp).Length;
                if (size > 0 && got != size) { File.Delete(temp); Finish(done, false, "The download was incomplete. Try again."); yield break; }
                // Replace an older copy here; the app can't be running from this folder while we do (if it is, keep the new one beside it).
                if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch { target = Path.Combine(InstallDir, Path.GetFileNameWithoutExtension(assetName ?? "NomadModDownloader-new") + ".exe"); if (File.Exists(target)) File.Delete(target); }
                }
                File.Move(temp, target);
                Downloader.Register(target);
                ModManagerPlugin.Log.LogMessage($"Downloaded Nomad Mod Downloader {version} from GitHub to {target}");
            }
            catch (Exception e) { Finish(done, false, "Couldn't save the app: " + e.Message); yield break; }

            Progress = 1;
            Finish(done, Downloader.Show(), $"Nomad Mod Downloader {version} is installed in {InstallDir}.");
        }

        private static void Finish(Action<bool, string> done, bool ok, string message)
        {
            Busy = false;
            Status = message;
            if (!ok) ModManagerPlugin.Log.LogWarning("Getting the downloader: " + message);
            try { done?.Invoke(ok, message); } catch { }
        }
    }
}
