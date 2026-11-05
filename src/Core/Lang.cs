using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NomadModDownloader.Core;

/// <summary>
/// App text in the chosen language. Strings are looked up by their English text, so anything
/// not translated simply shows in English. Translations live in Lang\xx.json (embedded).
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name, string MyMemoryCode);

    public static readonly IReadOnlyList<Language> Languages = new[]
    {
        new Language("en", "English", "en-GB"),
        new Language("es", "Español", "es-ES"),
        new Language("de", "Deutsch", "de-DE"),
        new Language("fr", "Français", "fr-FR"),
        new Language("pt", "Português", "pt-BR"),
        new Language("it", "Italiano", "it-IT"),
        new Language("ru", "Русский", "ru-RU"),
        new Language("pl", "Polski", "pl-PL"),
        new Language("tr", "Türkçe", "tr-TR"),
        new Language("zh", "简体中文", "zh-CN"),
        new Language("ja", "日本語", "ja-JP"),
        new Language("ko", "한국어", "ko-KR"),
    };

    public static Language Current { get; private set; } = Languages[0];
    public static bool IsEnglish => Current.Code == "en";

    private static Dictionary<string, string> _strings = new();

    /// <summary>Picks the saved language, or Windows' display language the first time.</summary>
    public static void Init(string? saved)
    {
        var code = saved;
        if (string.IsNullOrEmpty(code))
        {
            var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            code = Languages.Any(l => l.Code == sys) ? sys : "en";
        }
        Current = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _strings = IsEnglish ? new() : Load<Dictionary<string, string>>($"Lang.{Current.Code}.json") ?? new();
    }

    /// <summary>Translate a piece of app text.</summary>
    public static string T(string english) =>
        _strings.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t) ? t : english;

    /// <summary>Translate, then fill in {0}, {1}… (the English text is the key, with the placeholders in it).</summary>
    public static string F(string english, params object?[] args)
    {
        var fmt = T(english);
        try { return string.Format(fmt, args); }
        catch (FormatException) { return string.Format(english, args); } // a broken translation never crashes the app
    }

    private static T? Load<T>(string resource) where T : class
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            return s == null ? null : JsonSerializer.Deserialize<T>(s);
        }
        catch { return null; }
    }
}
