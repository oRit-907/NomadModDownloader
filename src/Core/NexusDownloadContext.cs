using System.Text.RegularExpressions;
using System.Web;

namespace NomadModDownloader.Core;

/// <summary>
/// Works out which mod and file a "Manual download" belongs to, from the Nexus page it was
/// started on (…/nomaddrive/mods/13?tab=files&amp;file_id=67) and Nexus's file naming
/// ("Nomad Cruise Control-13-1-3-0-1793172246.zip": name-modId-version-uploadTime.ext).
/// </summary>
public sealed record NexusDownloadContext(int? ModId, int? FileId, long? UploadUnix)
{
    private static readonly Regex PagePath = new(@"/(?:games/)?nomaddrive/mods/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Stamp = new(@"-(\d{9,11})(?:\s*\(\d+\))?\.[A-Za-z0-9]+$");
    private static readonly Regex ModInName = new(@"-(\d+)-");

    public static bool IsNomadDrivePage(string? url) => url != null && PagePath.IsMatch(url) ||
        (url?.Contains("/nomaddrive", StringComparison.OrdinalIgnoreCase) ?? false);

    public static NexusDownloadContext From(string? pageUrl, string fileName)
    {
        int? modId = null, fileId = null;
        if (!string.IsNullOrEmpty(pageUrl) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var u))
        {
            var m = PagePath.Match(u.AbsolutePath);
            if (m.Success) modId = int.Parse(m.Groups[1].Value);
            var q = HttpUtility.ParseQueryString(u.Query);
            if (int.TryParse(q["file_id"] ?? q["id"], out var f)) fileId = f;
        }

        long? stamp = null;
        var s = Stamp.Match(fileName);
        if (s.Success) stamp = long.Parse(s.Groups[1].Value);

        if (modId == null && s.Success)
        {
            // Name-<modId>-<version parts>-<stamp>: the mod id is the first all-digit part we can find after the name.
            var head = fileName[..s.Index];
            var m = ModInName.Match(head + "-");
            if (m.Success) modId = int.Parse(m.Groups[1].Value);
        }
        return new NexusDownloadContext(modId, fileId, stamp);
    }

    /// <summary>The file entry this download is: by file id, else by upload time, else the mod's main file.</summary>
    public ModFileInfo? PickFile(IReadOnlyList<ModFileInfo> files)
    {
        if (FileId != null && files.FirstOrDefault(f => f.FileId == FileId) is { } byId) return byId;
        if (UploadUnix != null)
        {
            var byTime = files.FirstOrDefault(f => Math.Abs(new DateTimeOffset(DateTime.SpecifyKind(f.Date, DateTimeKind.Utc)).ToUnixTimeSeconds() - UploadUnix.Value) <= 2);
            if (byTime != null) return byTime;
        }
        return files.FirstOrDefault(f => f.IsCurrent && f.Primary) ?? files.FirstOrDefault(f => f.IsCurrent);
    }
}
