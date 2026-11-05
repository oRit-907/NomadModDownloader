using System.Web;

namespace NomadModDownloader.Core;

/// <summary>
/// A Nexus "Mod Manager Download" link, e.g.
/// nxm://nomaddrive/mods/13/files/67?key=abc&amp;expires=1730000000&amp;user_id=123
/// </summary>
public sealed record NxmLink(string GameDomain, int ModId, int FileId, string? Key, string? Expires)
{
    public bool IsNomadDrive => string.Equals(GameDomain, NexusApi.GameDomain, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out NxmLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Trim('"');
        if (!text.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // expected: mods/{modId}/files/{fileId}
        if (segs.Length < 4 || !segs[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segs[2].Equals("files", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(segs[1], out var modId) || !int.TryParse(segs[3], out var fileId)) return false;

        var q = HttpUtility.ParseQueryString(uri.Query);
        link = new NxmLink(uri.Host, modId, fileId, q["key"], q["expires"]);
        return true;
    }

    public static string? FindInArgs(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.TrimStart('"').StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
}
