using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Mono.Cecil;
#if BEPINEX6
using BepInEx.Unity.Mono;
#endif

namespace NomadModManager
{
    /// <summary>One setting of a mod, as BepInEx describes it.</summary>
    internal sealed class Setting
    {
        public string Section = "", Key = "", Description = "", TypeName = "", Value = "", Default = "";
        public List<string> Options;            // a fixed list (enum values or "Acceptable values")
        public double? Min, Max;                // "Acceptable value range"
        public ConfigEntryBase Live;            // set when the mod is running: changes apply right away

        public bool IsBool => TypeName == "Boolean";
        public bool IsInteger => TypeName == "Int32" || TypeName == "Int64" || TypeName == "Int16" || TypeName == "Byte" || TypeName == "UInt32" || TypeName == "UInt16" || TypeName == "SByte";
        public bool IsNumber => IsInteger || TypeName == "Single" || TypeName == "Double" || TypeName == "Decimal";
        public bool IsKey => TypeName == "KeyboardShortcut" || TypeName == "KeyCode";
        public bool IsDefault => string.Equals(Value, Default, StringComparison.Ordinal);

        /// <summary>A sensible step for the − / + buttons.</summary>
        public double Step
        {
            get
            {
                if (IsInteger) return Min.HasValue && Max.HasValue && Max - Min > 200 ? Math.Max(1, Math.Round((Max.Value - Min.Value) / 100)) : 1;
                if (Min.HasValue && Max.HasValue) return NiceStep((Max.Value - Min.Value) / 20);
                var v = Math.Abs(Num(Value) ?? 1);
                return v >= 100 ? 10 : v >= 10 ? 1 : 0.1;
            }
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 0.1;
            var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var m = raw / p;
            return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p;
        }

        public static double? Num(string s) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
    }

    /// <summary>The settings of one plugin: from the running plugin (live) or from its .cfg file.</summary>
    internal sealed class ModSettings
    {
        public string Guid = "", Title = "", File = "";
        public bool Live;
        public readonly List<Setting> Items = new List<Setting>();

        // ------------------------------------------------------------------
        // Finding a mod's settings

        private static readonly Dictionary<string, (DateTime Time, List<(string Guid, string Name)> Plugins)> GuidCache =
            new Dictionary<string, (DateTime, List<(string, string)>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every plugin in the mod (running or not) that has settings.</summary>
        public static List<ModSettings> For(ModEntry m)
        {
            var result = new List<ModSettings>();
            foreach (var (guid, name) in PluginsOf(m))
            {
                var s = Load(guid, name);
                if (s != null && s.Items.Count > 0) result.Add(s);
            }
            return result;
        }

        /// <summary>Quick check used for the Settings button: is there a config file or a running config?</summary>
        public static bool Has(ModEntry m)
        {
            foreach (var (guid, _) in PluginsOf(m))
            {
                if (System.IO.File.Exists(CfgPath(guid))) return true;
                if (LiveConfig(guid) is ConfigFile cf && cf.Keys.Count > 0) return true;
            }
            return false;
        }

        public static string CfgPath(string guid) => Path.Combine(Paths.ConfigPath, guid + ".cfg");

        public static List<(string Guid, string Name)> PluginsOf(ModEntry m)
        {
            var list = new List<(string, string)>();
            foreach (var g in m.Guids)
                if (!list.Any(x => x.Item1 == g)) list.Add((g, Bep.Plugins.TryGetValue(g, out var info) ? info.Metadata.Name : m.Name));
            foreach (var dll in m.Dlls.Concat(m.OffDlls))
                foreach (var p in ReadPlugins(dll))
                    if (!list.Any(x => x.Item1 == p.Guid)) list.Add(p);
            return list;
        }

        private static List<(string Guid, string Name)> ReadPlugins(string dll)
        {
            try
            {
                var t = System.IO.File.GetLastWriteTimeUtc(dll);
                if (GuidCache.TryGetValue(dll, out var c) && c.Time == t) return c.Plugins;
                var found = new List<(string, string)>();
                using (var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
                {
                    foreach (var type in asm.MainModule.Types)
                    {
                        var a = type.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == "BepInEx.BepInPlugin" && x.ConstructorArguments.Count == 3);
                        if (a?.ConstructorArguments[0].Value is string g && g.Length > 0)
                            found.Add((g, a.ConstructorArguments[1].Value as string ?? g));
                    }
                }
                GuidCache[dll] = (t, found);
                return found;
            }
            catch { return new List<(string, string)>(); }
        }

        private static ConfigFile LiveConfig(string guid)
        {
            try
            {
                if (!Bep.Plugins.TryGetValue(guid, out var info)) return null;
                return (info.Instance as BaseUnityPlugin)?.Config;
            }
            catch { return null; }
        }

        public static ModSettings Load(string guid, string name)
        {
            var s = new ModSettings { Guid = guid, Title = name, File = CfgPath(guid) };
            var live = LiveConfig(guid);
            if (live != null && live.Keys.Count > 0)
            {
                s.Live = true;
                foreach (var def in live.Keys.ToList())
                {
                    ConfigEntryBase e;
                    try { e = live[def]; } catch { continue; }
                    s.Items.Add(FromLive(e));
                }
            }
            else if (System.IO.File.Exists(s.File)) s.Items.AddRange(ParseFile(s.File));
            // Group by section, keeping the mod's own order inside each.
            var order = s.Items.Select(i => i.Section).Distinct().ToList();
            var sorted = s.Items.Select((x, i) => (x, i)).OrderBy(p => order.IndexOf(p.x.Section)).ThenBy(p => p.i).Select(p => p.x).ToList();
            s.Items.Clear();
            s.Items.AddRange(sorted);
            return s;
        }

        private static Setting FromLive(ConfigEntryBase e)
        {
            var st = new Setting
            {
                Section = e.Definition.Section, Key = e.Definition.Key, Live = e,
                Description = e.Description?.Description ?? "", TypeName = e.SettingType.Name,
                Value = ToText(e.BoxedValue, e.SettingType), Default = ToText(e.DefaultValue, e.SettingType),
            };
            if (e.SettingType.IsEnum) st.Options = Enum.GetNames(e.SettingType).ToList();
            var acc = e.Description?.AcceptableValues;
            if (acc != null)
            {
                var t = acc.GetType();
                if (t.Name.StartsWith("AcceptableValueRange"))
                {
                    st.Min = Setting.Num(Convert.ToString(t.GetProperty("MinValue")?.GetValue(acc, null), CultureInfo.InvariantCulture));
                    st.Max = Setting.Num(Convert.ToString(t.GetProperty("MaxValue")?.GetValue(acc, null), CultureInfo.InvariantCulture));
                }
                else if (t.Name.StartsWith("AcceptableValueList") && t.GetProperty("AcceptableValues")?.GetValue(acc, null) is Array arr)
                {
                    st.Options = arr.Cast<object>().Select(o => ToText(o, e.SettingType)).ToList();
                }
            }
            return st;
        }

        private static string ToText(object v, Type t)
        {
            try { return TomlTypeConverter.ConvertToString(v, t); }
            catch { return Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; }
        }

        /// <summary>
        /// Reads a BepInEx .cfg file: [Section], "## description", "# Setting type:", "# Default value:",
        /// "# Acceptable values:" / "# Acceptable value range: From a to b", then "Key = Value".
        /// </summary>
        public static List<Setting> ParseFile(string path)
        {
            var list = new List<Setting>();
            string[] lines;
            try { lines = System.IO.File.ReadAllLines(path); } catch { return list; }
            var section = "";
            var cur = new Setting();
            foreach (var raw in lines)
            {
                var l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("[") && l.EndsWith("]")) { section = l.Substring(1, l.Length - 2); cur = new Setting(); continue; }
                if (l.StartsWith("##")) { cur.Description += (cur.Description.Length > 0 ? " " : "") + l.Substring(2).Trim(); continue; }
                if (l.StartsWith("#"))
                {
                    var c = l.Substring(1).Trim();
                    if (c.StartsWith("Setting type:")) cur.TypeName = c.Substring("Setting type:".Length).Trim();
                    else if (c.StartsWith("Default value:")) cur.Default = c.Substring("Default value:".Length).Trim();
                    else if (c.StartsWith("Acceptable values:")) cur.Options = c.Substring("Acceptable values:".Length).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    else if (c.StartsWith("Acceptable value range:"))
                    {
                        var r = c.Substring("Acceptable value range:".Length).Trim(); // "From 0 to 10"
                        var parts = r.Replace("From", "").Split(new[] { " to " }, StringSplitOptions.None);
                        if (parts.Length == 2) { cur.Min = Setting.Num(parts[0]); cur.Max = Setting.Num(parts[1]); }
                    }
                    continue;
                }
                var eq = l.IndexOf('=');
                if (eq <= 0) continue;
                cur.Section = section;
                cur.Key = l.Substring(0, eq).Trim();
                cur.Value = l.Substring(eq + 1).Trim();
                if (cur.TypeName == "Boolean" && cur.Options != null) cur.Options = null;
                list.Add(cur);
                cur = new Setting();
            }
            return list;
        }

        // ------------------------------------------------------------------
        // Changing a setting

        /// <summary>Sets a value. Returns null when it worked, or what went wrong.</summary>
        public string Set(Setting s, string value)
        {
            if (s.Min.HasValue || s.Max.HasValue)
            {
                var n = Setting.Num(value);
                if (n == null) return "Not a number";
                var v = n.Value;
                if (s.Min.HasValue && v < s.Min.Value) v = s.Min.Value;
                if (s.Max.HasValue && v > s.Max.Value) v = s.Max.Value;
                value = s.IsInteger ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);
            }
            else if (s.IsInteger && Setting.Num(value) is double iv) value = Math.Round(iv).ToString(CultureInfo.InvariantCulture);

            if (s.Live != null)
            {
                try
                {
                    s.Live.BoxedValue = TomlTypeConverter.ConvertToValue(value, s.Live.SettingType);
                    s.Live.ConfigFile.Save();
                    s.Value = ToText(s.Live.BoxedValue, s.Live.SettingType);
                    return null;
                }
                catch (Exception e) { return "Couldn't use that value (" + e.GetBaseException().Message + ")"; }
            }
            try
            {
                var lines = System.IO.File.ReadAllLines(File).ToList();
                var section = "";
                for (var i = 0; i < lines.Count; i++)
                {
                    var l = lines[i].Trim();
                    if (l.StartsWith("[") && l.EndsWith("]")) { section = l.Substring(1, l.Length - 2); continue; }
                    if (section != s.Section || l.StartsWith("#")) continue;
                    var eq = l.IndexOf('=');
                    if (eq <= 0 || l.Substring(0, eq).Trim() != s.Key) continue;
                    lines[i] = s.Key + " = " + value;
                    System.IO.File.WriteAllLines(File, lines.ToArray());
                    s.Value = value;
                    return null;
                }
                return "That setting isn't in the file any more";
            }
            catch (Exception e) { return "Couldn't save (" + e.Message + ")"; }
        }

        public string Reset(Setting s) => Set(s, s.Default);
    }
}
