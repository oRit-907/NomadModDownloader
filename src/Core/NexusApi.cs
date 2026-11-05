using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NomadModDownloader.Core;

public sealed class ModInfo
{
    public int ModId { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public long Downloads { get; set; }
    public long Endorsements { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? PictureUrl { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string PageUrl => $"https://www.nexusmods.com/{NexusApi.GameDomain}/mods/{ModId}";
}

public sealed class ModFileInfo
{
    public int FileId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Category { get; set; } = "";   // MAIN, UPDATE, OPTIONAL, OLD_VERSION, MISCELLANEOUS, ARCHIVED
    public DateTime Date { get; set; }
    public long SizeBytes { get; set; }
    public bool Primary { get; set; }
    public string Description { get; set; } = "";
    public List<string> Changelog { get; set; } = new();

    public bool IsCurrent => Category is not ("OLD_VERSION" or "ARCHIVED" or "REMOVED");

    public override string ToString()
    {
        var cat = Category switch { "MAIN" => "Main", "OPTIONAL" => "Optional", "UPDATE" => "Update", "MISCELLANEOUS" => "Misc", "OLD_VERSION" => "Old", _ => Category };
        return $"{Name}  v{Version}  [{cat}]  {Fmt.Size(SizeBytes)}";
    }
}

/// <summary>Another mod (or an outside download) a mod says it needs.</summary>
public sealed record ModRequirement(int ModId, string Name, bool Optional, bool External, string Url, string Notes)
{
    /// <summary>The BepInEx pack: the app installs BepInEx itself, so it's never asked for.</summary>
    public bool IsBepInExPack => ModId == 7 || (Name.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) && Name.Contains("Pack", StringComparison.OrdinalIgnoreCase));
}

public sealed class NexusUser
{
    public string Name { get; set; } = "";
    public bool IsPremium { get; set; }
}

public enum ModSort { RecentlyUpdated, MostDownloaded, MostEndorsed, Newest }

public sealed class NexusException : Exception
{
    public HttpStatusCode? Status { get; }
    public NexusException(string msg, HttpStatusCode? status = null) : base(msg) { Status = status; }
}

/// <summary>
/// Nexus Mods access, locked to Nomad Drive.
/// Browsing uses the public v2 GraphQL API (no key needed).
/// Downloading uses the v1 REST API with the user's own API key:
///   Premium  -> direct download links.
///   Free     -> a key+expires pair from the "Mod Manager Download" button (nxm:// link).
/// </summary>
public sealed class NexusApi
{
    public const string GameDomain = "nomaddrive";
    public const int GameId = 9724;
    public const string AppName = "NomadModDownloader";
    public static readonly string AppVersion = typeof(NexusApi).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private const string GraphQl = "https://api.nexusmods.com/v2/graphql";
    private const string V1 = "https://api.nexusmods.com/v1";

    private readonly HttpClient _http;
    public string? ApiKey { get; set; }

    public NexusApi(HttpClient http) { _http = http; }

    public static HttpClient CreateHttpClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName}/{AppVersion}");
        return h;
    }

    // ---------------- GraphQL (browse) ----------------

    private async Task<JsonNode> QueryAsync(string query, object? variables, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphQl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("Application-Name", AppName);
        req.Headers.Add("Application-Version", AppVersion);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new NexusException(L.F("Nexus browse request failed ({0}).", (int)resp.StatusCode), resp.StatusCode);
        var root = JsonNode.Parse(text) ?? throw new NexusException(L.T("Empty response from Nexus."));
        if (root["errors"] is JsonArray errs && errs.Count > 0)
            throw new NexusException("Nexus: " + (errs[0]?["message"]?.GetValue<string>() ?? L.T("query error")));
        return root["data"] ?? throw new NexusException(L.T("Nexus returned no data."));
    }

    private const string ModFields = "modId name summary version author downloads endorsements thumbnailUrl pictureUrl updatedAt modCategory { name }";

    public async Task<(List<ModInfo> Mods, int Total)> SearchModsAsync(string? search, ModSort sort, int offset, int count, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object>
        {
            ["gameDomainName"] = new[] { new { value = GameDomain, op = "EQUALS" } }
        };
        if (!string.IsNullOrWhiteSpace(search))
            filter["name"] = new[] { new { value = search.Trim(), op = "WILDCARD" } };

        var sortKey = sort switch
        {
            ModSort.MostDownloaded => "downloads",
            ModSort.MostEndorsed => "endorsements",
            ModSort.Newest => "createdAt",
            _ => "updatedAt"
        };
        var sortArr = new[] { new Dictionary<string, object> { [sortKey] = new { direction = "DESC" } } };

        var data = await QueryAsync(
            "query($f: ModsFilter, $s: [ModsSort!], $o: Int, $c: Int) { mods(filter: $f, sort: $s, offset: $o, count: $c) { totalCount nodes { " + ModFields + " } } }",
            new { f = filter, s = sortArr, o = offset, c = count }, ct);

        var mods = new List<ModInfo>();
        foreach (var n in data["mods"]?["nodes"]?.AsArray() ?? new JsonArray())
            if (n != null) mods.Add(ParseMod(n));
        var total = data["mods"]?["totalCount"]?.GetValue<int>() ?? mods.Count;
        return (mods, total);
    }

    public async Task<ModInfo?> GetModAsync(int modId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query($m: ID!, $g: ID!) { mod(modId: $m, gameId: $g) { " + ModFields + " } }",
            new { m = modId, g = GameId }, ct);
        var n = data["mod"];
        return n == null ? null : ParseMod(n);
    }

    public async Task<List<ModFileInfo>> GetModFilesAsync(int modId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query($m: ID!, $g: ID!) { modFiles(modId: $m, gameId: $g) { fileId name version category date sizeInBytes primary description changelogText } }",
            new { m = modId, g = GameId }, ct);
        var list = new List<ModFileInfo>();
        foreach (var n in data["modFiles"]?.AsArray() ?? new JsonArray())
        {
            if (n == null) continue;
            list.Add(new ModFileInfo
            {
                FileId = n["fileId"]!.GetValue<int>(),
                Name = Str(n["name"]),
                Version = Str(n["version"]),
                Category = Str(n["category"]).ToUpperInvariant(),
                Date = DateTimeOffset.FromUnixTimeSeconds(Num(n["date"])).UtcDateTime,
                SizeBytes = Num(n["sizeInBytes"]),
                Primary = Num(n["primary"]) == 1,
                Description = Str(n["description"]),
                Changelog = (n["changelogText"] as JsonArray)?.Select(c => c?.GetValueKind() == JsonValueKind.String ? WebUtility.HtmlDecode(c.GetValue<string>()).Trim() : "")
                                .Where(c => c.Length > 0).ToList() ?? new()
            });
        }
        // Primary first, then current files newest first, old versions last.
        return list.OrderByDescending(f => f.IsCurrent).ThenByDescending(f => f.Primary).ThenByDescending(f => f.Date).ToList();
    }

    /// <summary>What the mod's page lists under "Requirements" (Nomad Drive mods and outside downloads).</summary>
    public async Task<List<ModRequirement>> GetRequirementsAsync(int modId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query($m: ID!, $g: ID!) { mod(modId: $m, gameId: $g) { modRequirements { nexusRequirements { nodes { modId modName gameId externalRequirement url notes } } } } }",
            new { m = modId, g = GameId }, ct);
        var list = new List<ModRequirement>();
        foreach (var n in data["mod"]?["modRequirements"]?["nexusRequirements"]?["nodes"]?.AsArray() ?? new JsonArray())
        {
            if (n == null) continue;
            var external = n["externalRequirement"]?.GetValue<bool>() == true;
            var game = Str(n["gameId"]);
            int.TryParse(Str(n["modId"]), out var id);
            if (!external && game.Length > 0 && game != GameId.ToString()) external = true; // a mod for another game: treat as an outside link
            var notes = WebUtility.HtmlDecode(Str(n["notes"])).Trim();
            var optional = notes.Contains("optional", StringComparison.OrdinalIgnoreCase);
            var url = Str(n["url"]);
            if (url.Length == 0 && !external && id > 0) url = $"https://www.nexusmods.com/{GameDomain}/mods/{id}";
            list.Add(new ModRequirement(external ? 0 : id, Str(n["modName"]), optional, external, url, notes));
        }
        return list;
    }

    private static ModInfo ParseMod(JsonNode n) => new()
    {
        ModId = n["modId"]!.GetValue<int>(),
        Name = Str(n["name"]),
        Summary = WebUtility.HtmlDecode(Str(n["summary"])),
        Version = Str(n["version"]),
        Author = Str(n["author"]),
        Category = Str(n["modCategory"]?["name"]),
        Downloads = Num(n["downloads"]),
        Endorsements = Num(n["endorsements"]),
        ThumbnailUrl = n["thumbnailUrl"]?.GetValue<string>(),
        PictureUrl = n["pictureUrl"]?.GetValue<string>(),
        UpdatedAt = DateTime.TryParse(Str(n["updatedAt"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null
    };

    private static string Str(JsonNode? n) => n == null ? "" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
    private static long Num(JsonNode? n)
    {
        if (n == null) return 0;
        return n.GetValueKind() switch
        {
            JsonValueKind.Number => n.GetValue<long>(),
            JsonValueKind.String => long.TryParse(n.GetValue<string>(), out var v) ? v : 0,
            JsonValueKind.True => 1,
            _ => 0
        };
    }

    // ---------------- v1 REST (account + downloads) ----------------

    private HttpRequestMessage V1Request(string path)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new NexusException(L.T("No Nexus API key set. Add your personal API key first."));
        var req = new HttpRequestMessage(HttpMethod.Get, V1 + path);
        req.Headers.Add("apikey", ApiKey);
        req.Headers.Add("Application-Name", AppName);
        req.Headers.Add("Application-Version", AppVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public async Task<NexusUser> ValidateAsync(CancellationToken ct = default)
    {
        using var req = V1Request("/users/validate.json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new NexusException(L.T("That API key was rejected by Nexus."), resp.StatusCode);
        if (!resp.IsSuccessStatusCode) throw new NexusException(L.F("Could not check the API key ({0}).", (int)resp.StatusCode), resp.StatusCode);
        var j = JsonNode.Parse(text)!;
        return new NexusUser
        {
            Name = Str(j["name"]),
            IsPremium = j["is_premium"]?.GetValue<bool>() == true || j["is_premium?"]?.GetValue<bool>() == true
        };
    }

    /// <summary>Gets a CDN URL for a file. Free accounts must pass the key/expires from an nxm:// link.</summary>
    public async Task<Uri> GetDownloadUrlAsync(int modId, int fileId, string? nxmKey, string? expires, CancellationToken ct = default)
    {
        var path = $"/games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(expires))
            path += $"?key={Uri.EscapeDataString(nxmKey)}&expires={Uri.EscapeDataString(expires)}";

        using var req = V1Request(path);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new NexusException(string.IsNullOrEmpty(nxmKey)
                ? "Nexus only gives direct downloads to Premium accounts. Use the \"Mod Manager Download\" button on the mod's Nexus page instead."
                : "Nexus refused that download link (it may have expired). Click \"Mod Manager Download\" again.", resp.StatusCode);
        if (resp.StatusCode == HttpStatusCode.Gone) throw new NexusException(L.T("That download link has expired. Click \"Mod Manager Download\" again."), resp.StatusCode);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new NexusException(L.T("That file no longer exists on Nexus."), resp.StatusCode);
        if (resp.StatusCode == (HttpStatusCode)429) throw new NexusException(L.T("Nexus rate limit reached. Try again in a little while."), resp.StatusCode);
        if (!resp.IsSuccessStatusCode) throw new NexusException(L.F("Nexus download request failed ({0}).", (int)resp.StatusCode), resp.StatusCode);

        var arr = JsonNode.Parse(text)?.AsArray();
        var uri = arr?.Select(x => x?["URI"]?.GetValue<string>()).FirstOrDefault(u => !string.IsNullOrEmpty(u));
        if (uri == null) throw new NexusException(L.T("Nexus returned no download server."));
        return new Uri(uri);
    }

    // ---------------- downloading ----------------

    public static async Task<string> DownloadFileAsync(HttpClient http, Uri url, string folder, string? preferredName, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var name = resp.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                   ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = (preferredName ?? "download") + ".zip";
        name = Fmt.SafeFileName(name);

        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, name);
        var tmp = dest + ".part";
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buf = new byte[81920];
            long done = 0; int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(tmp, dest, true);
        progress?.Report(1);
        return dest;
    }
}

public static class Fmt
{
    /// <summary>For narrow columns: "4 Nov" / "11-04" this year, otherwise the full date.</summary>
    public static string ShortDate(DateTime d) =>
        d.Year != DateTime.Now.Year ? Date(d, shortYear: true) : L.IsEnglish ? d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture) : d.ToString("MM-dd");

    public static string Num(double? d) => d?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>"4 Nov 2026" in English; "2026-11-04" in other languages (month names would otherwise stay English).</summary>
    public static string Date(DateTime d, bool shortYear = false) =>
        L.IsEnglish ? d.ToString(shortYear ? "d MMM yy" : "d MMM yyyy") : d.ToString(shortYear ? "yy-MM-dd" : "yyyy-MM-dd");

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    public static string Count(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}K" : n.ToString();

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(name) ? "mod" : name;
    }
}
