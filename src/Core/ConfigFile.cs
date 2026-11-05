using System.Globalization;
using System.Text.RegularExpressions;

namespace NomadModDownloader.Core;

public enum ConfigKind { Text, Bool, Number, Choice, Key, Color }

/// <summary>One setting in a BepInEx .cfg file, with what BepInEx wrote about it in the comments above.</summary>
public sealed class ConfigEntry
{
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string Value { get; set; } = "";
    public string Description { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string? Default { get; init; }
    public List<string> Options { get; init; } = new();
    public bool Flags { get; init; }               // "# Multiple values can be set at the same time"
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool IsInteger { get; init; }
    internal int Line { get; init; }

    public ConfigKind Kind =>
        TypeName == "Boolean" ? ConfigKind.Bool
        : TypeName == "KeyCode" || TypeName == "KeyboardShortcut" ? ConfigKind.Key
        : TypeName == "Color" ? ConfigKind.Color
        : Options.Count > 0 && !Flags ? ConfigKind.Choice
        : TypeName is "Int32" or "Single" or "Double" or "Int64" or "Byte" or "Int16" or "UInt32" or "Decimal" ? ConfigKind.Number
        : ConfigKind.Text;

    /// <summary>Checks a new value against what the setting accepts; returns null if it's fine, else why not.</summary>
    public string? Validate(string value)
    {
        switch (Kind)
        {
            case ConfigKind.Bool:
                return value is "true" or "false" ? null : L.T("Must be true or false.");
            case ConfigKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return L.T("Must be a number.");
                if (IsInteger && d != Math.Floor(d)) return L.T("Must be a whole number.");
                if (Min is { } lo && d < lo || Max is { } hi && d > hi) return L.F("Must be between {0} and {1}.", Fmt.Num(Min), Fmt.Num(Max));
                return null;
            case ConfigKind.Choice:
                return Options.Contains(value) ? null : L.F("Must be one of: {0}", string.Join(", ", Options));
            default:
                return value.Contains('\n') ? L.T("Must be on one line.") : null;
        }
    }
}

/// <summary>
/// A BepInEx config file (BepInEx\config\&lt;plugin id&gt;.cfg). Edits change only the values,
/// keeping every comment and line as BepInEx wrote them.
/// </summary>
public sealed class ConfigFile
{
    private static readonly Regex Section = new(@"^\s*\[(.+)\]\s*$");
    private static readonly Regex Setting = new(@"^\s*([^#=][^=]*?)\s*=\s?(.*)$");

    public string Path { get; }
    public List<ConfigEntry> Entries { get; } = new();
    private readonly List<string> _lines;
    private readonly string _newline;

    private ConfigFile(string path, string text)
    {
        Path = path;
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        Parse();
    }

    public static ConfigFile Load(string path) => new(path, File.ReadAllText(path));
    public static ConfigFile FromText(string text, string path = "test.cfg") => new(path, text);

    private void Parse()
    {
        var section = "";
        var desc = new List<string>();
        string type = "", def = "";
        bool hasDef = false, flags = false;
        var options = new List<string>();
        double? min = null, max = null;

        void Reset() { desc.Clear(); type = ""; def = ""; hasDef = false; flags = false; options = new(); min = max = null; }

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i].Trim();
            if (line.Length == 0) { continue; }
            if (Section.Match(line) is { Success: true } sm) { section = sm.Groups[1].Value.Trim(); Reset(); continue; }
            if (line.StartsWith("##")) { desc.Add(line[2..].Trim()); continue; }
            if (line.StartsWith('#'))
            {
                var c = line[1..].Trim();
                if (c.StartsWith("Setting type:")) type = c["Setting type:".Length..].Trim();
                else if (c.StartsWith("Default value:")) { def = c["Default value:".Length..].Trim(); hasDef = true; }
                else if (c.StartsWith("Acceptable values:")) options = c["Acceptable values:".Length..].Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
                else if (c.StartsWith("Multiple values can be set")) flags = true;
                else if (Regex.Match(c, @"^Acceptable value range:\s*From\s+(\S+)\s+to\s+(\S+)") is { Success: true } rm)
                {
                    if (double.TryParse(rm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) min = a;
                    if (double.TryParse(rm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) max = b;
                }
                continue;
            }
            if (Setting.Match(_lines[i]) is { Success: true } st)
            {
                Entries.Add(new ConfigEntry
                {
                    Section = section, Key = st.Groups[1].Value.Trim(), Value = st.Groups[2].Value.Trim(),
                    Description = string.Join(" ", desc), TypeName = type, Default = hasDef ? def : null,
                    Options = options, Flags = flags, Min = min, Max = max, Line = i,
                    IsInteger = type is "Int32" or "Int64" or "Byte" or "Int16" or "UInt32"
                });
                Reset();
            }
        }
    }

    /// <summary>Changes a value in memory (call <see cref="Save"/> to write it). Returns why not, if the value isn't allowed.</summary>
    public string? Set(ConfigEntry entry, string value)
    {
        value = value.Trim();
        if (entry.Validate(value) is { } err) return err;
        entry.Value = value;
        var line = _lines[entry.Line];
        var eq = line.IndexOf('=');
        _lines[entry.Line] = line[..(eq + 1)] + " " + value;
        return null;
    }

    public string Text => string.Join(_newline, _lines);

    public void Save()
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Text);
        File.Move(tmp, Path, true);
    }

    /// <summary>Config files in BepInEx\config, each matched to the plugin that owns it (by plugin id) when possible.</summary>
    public static List<(string Path, PluginInfo? Plugin)> Find(GameInstall game)
    {
        var dir = System.IO.Path.Combine(game.BepInExDir, "config");
        var list = new List<(string, PluginInfo?)>();
        if (!Directory.Exists(dir)) return list;
        var plugins = PluginInspector.Scan(game, includeDisabled: true);
        foreach (var f in Directory.EnumerateFiles(dir, "*.cfg").OrderBy(f => f))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f);
            if (stem.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) { list.Add((f, null)); continue; }
            list.Add((f, plugins.FirstOrDefault(p => p.Guid.Equals(stem, StringComparison.OrdinalIgnoreCase))));
        }
        return list;
    }
}
