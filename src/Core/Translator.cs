using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

/// <summary>
/// Live translation of Nexus text (other people's mod summaries) with the free MyMemory service.
/// No account needed; it has a daily limit per PC. Results are cached on disk, so each summary
/// is only ever translated once per language.
/// </summary>
public sealed class Translator
{
    public const string ServiceName = "MyMemory";
    private const int MaxChunk = 450; // the service takes up to 500 bytes per request

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _cache;
    private readonly string _cacheFile = Path.Combine(AppPaths.Root, "translations.json");
    private DateTime _blockedUntil = DateTime.MinValue;

    public Translator(HttpClient http)
    {
        _http = http;
        try { _cache = File.Exists(_cacheFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile)) ?? new() : new(); }
        catch { _cache = new(); }
    }

    /// <summary>True when today's free limit is used up (until it resets).</summary>
    public bool LimitReached => DateTime.UtcNow < _blockedUntil;

    public bool TryCached(string text, out string translated) => _cache.TryGetValue(Key(text), out translated!);

    /// <summary>Translates English (or mostly English) Nexus text into the app language. Returns null if it can't right now.</summary>
    public async Task<string?> TranslateAsync(string text, CancellationToken ct)
    {
        if (L.IsEnglish || string.IsNullOrWhiteSpace(text)) return text;
        if (TryCached(text, out var hit)) return hit;
        if (LimitReached) return null;

        var parts = new List<string>();
        foreach (var chunk in Chunks(text))
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(chunk) +
                      "&langpair=" + Uri.EscapeDataString("en|" + L.Current.MyMemoryCode);
            string body;
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                // The daily limit comes back as HTTP 429 (with a JSON body saying so).
                if ((int)resp.StatusCode == 429) { _blockedUntil = DateTime.UtcNow.AddHours(6); return null; }
                if (!resp.IsSuccessStatusCode) { _blockedUntil = DateTime.UtcNow.AddMinutes(15); return null; }
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; } // offline / blocked: just show the original

            var j = JsonNode.Parse(body);
            var status = j?["responseStatus"]?.ToString();
            var outText = j?["responseData"]?["translatedText"]?.GetValue<string>() ?? "";
            if (status != "200" || outText.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) || outText.Contains("QUERY LENGTH LIMIT"))
            {
                // Daily limit (429) or another refusal: stop asking for a while.
                _blockedUntil = DateTime.UtcNow.AddHours(status == "429" || outText.StartsWith("MYMEMORY WARNING") ? 6 : 0.25);
                return null;
            }
            parts.Add(System.Net.WebUtility.HtmlDecode(outText));
        }

        var result = string.Join(" ", parts);
        _cache[Key(text)] = result;
        try { File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache)); } catch { }
        return result;
    }

    private static string Key(string text)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return L.Current.Code + ":" + hash;
    }

    /// <summary>Splits on sentence ends so each request stays under the size limit.</summary>
    internal static IEnumerable<string> Chunks(string text)
    {
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?。！？])\s+");
        var sb = new StringBuilder();
        foreach (var s0 in sentences)
        {
            var s = s0;
            while (Encoding.UTF8.GetByteCount(s) > MaxChunk)
            {
                // a very long "sentence": cut at a space
                var cut = s.LastIndexOf(' ', Math.Min(s.Length - 1, MaxChunk / 2));
                if (cut <= 0) cut = Math.Min(s.Length, MaxChunk / 3);
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return s[..cut].Trim();
                s = s[cut..].Trim();
            }
            if (Encoding.UTF8.GetByteCount(sb + " " + s) > MaxChunk && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
