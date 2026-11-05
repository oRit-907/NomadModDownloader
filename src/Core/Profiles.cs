using System.IO.Compression;
using System.Text.Json;

namespace NomadModDownloader.Core;

/// <summary>A saved set of mods: which are on. Mods not listed are switched off when it's applied.</summary>
public sealed class ModProfile
{
    public string Name { get; set; } = "";
    public List<string> Enabled { get; set; } = new();       // InstalledMod.Id values
    public List<string> EnabledOther { get; set; } = new();  // plugin folders/files added outside the app
    public List<SharedMod> Mods { get; set; } = new();       // Nexus mod + file, to reinstall missing ones
}

public sealed record SharedMod(int ModId, int FileId);

public sealed class ProfileStore
{
    public List<ModProfile> Profiles { get; set; } = new();
    public string? Active { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Root, "profiles.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ProfileStore Load(string? path = null)
    {
        try { var p = path ?? FilePath; if (File.Exists(p)) return JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(p)) ?? new(); }
        catch { }
        return new();
    }

    public void Save(string? path = null)
    {
        var p = path ?? FilePath;
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(p + ".tmp", p, true);
    }

    /// <summary>Saves what's switched on right now as a profile (replacing one with the same name).</summary>
    public ModProfile Capture(string name, Manifest manifest, GameInstall game)
    {
        var p = new ModProfile
        {
            Name = name,
            Enabled = manifest.Mods.Where(m => !m.Disabled).Select(m => m.Id).ToList(),
            EnabledOther = ModInstaller.FindUntracked(game, manifest).Where(u => !u.Disabled).Select(u => u.Rel).ToList(),
            Mods = manifest.Mods.Where(m => !m.Disabled && m.ModId > 0).Select(m => new SharedMod(m.ModId, m.FileId)).ToList()
        };
        Profiles.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Profiles.Add(p);
        Active = name;
        return p;
    }

    /// <summary>Turns mods on and off to match the profile. Returns the profile's Nexus mods that aren't installed any more.</summary>
    public static List<SharedMod> Apply(ModProfile profile, Manifest manifest, GameInstall game, string? manifestPath = null)
    {
        foreach (var m in manifest.Mods.ToList())
        {
            var want = profile.Enabled.Contains(m.Id);
            if (m.Disabled == want) ModInstaller.SetDisabled(m, !want, game, manifest, manifestPath);
        }
        foreach (var u in ModInstaller.FindUntracked(game, manifest))
        {
            var want = profile.EnabledOther.Contains(u.Rel);
            if (u.Disabled == want) ModInstaller.SetUntrackedDisabled(game, u.Rel, !want);
        }
        var have = manifest.Mods.Select(m => m.ModId).ToHashSet();
        return profile.Mods.Where(s => !have.Contains(s.ModId)).ToList();
    }
}

/// <summary>
/// A short text code for a list of Nexus mods (mod + file), so friends can install the same set.
/// Format: "NMD1-" + base64url(deflate(varint pairs)).
/// </summary>
public static class ShareCode
{
    public const string Prefix = "NMD1-";

    public static string Encode(IEnumerable<SharedMod> mods)
    {
        using var raw = new MemoryStream();
        foreach (var m in mods.DistinctBy(m => m.ModId)) { WriteVar(raw, (uint)m.ModId); WriteVar(raw, (uint)Math.Max(0, m.FileId)); }
        using var packed = new MemoryStream();
        using (var z = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) raw.WriteTo(z);
        return Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static List<SharedMod>? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = new string(code.Trim().Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            z.CopyTo(raw);
            raw.Position = 0;
            var list = new List<SharedMod>();
            while (raw.Position < raw.Length)
            {
                var mod = ReadVar(raw); var file = ReadVar(raw);
                if (mod is null || file is null || mod == 0 || list.Count > 500) return null;
                list.Add(new SharedMod((int)mod, (int)file));
            }
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private static void WriteVar(Stream s, uint v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static uint? ReadVar(Stream s)
    {
        uint v = 0; var shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0 || shift > 28) return null;
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }
}

public sealed record Conflict(string Message, List<string> Mods);

/// <summary>Spots mods that get in each other's way.</summary>
public static class ConflictChecker
{
    public static List<Conflict> Check(GameInstall game, Manifest manifest)
    {
        var list = new List<Conflict>();

        // 1. The same plugin installed more than once (BepInEx only loads one of them).
        var plugins = PluginInspector.Scan(game);
        foreach (var g in plugins.GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var where = g.Select(p => Path.GetRelativePath(game.Root, p.File)).ToList();
            list.Add(new Conflict(L.F("{0} is installed {1} times. BepInEx loads only one copy, so remove the others:", g.First().Name, g.Count()) + "\n  " + string.Join("\n  ", where),
                g.Select(p => OwnerName(game, manifest, p.File) ?? Path.GetFileName(p.File)).Distinct().ToList()));
        }

        // 2. Two installed mods wrote the same file (the later one replaced the earlier one's copy).
        var owners = new Dictionary<string, List<InstalledMod>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifest.Mods)
            foreach (var f in m.Files)
                (owners.TryGetValue(f, out var l) ? l : owners[f] = new()).Add(m);
        foreach (var g in owners.Where(o => o.Value.Count > 1 && !IsHarmless(o.Key)).GroupBy(o => string.Join("|", o.Value.Select(m => m.Id).OrderBy(x => x))))
        {
            var mods = g.First().Value;
            var files = g.Select(x => x.Key).ToList();
            list.Add(new Conflict(L.F("{0} and {1} both include {2} file(s), e.g. {3}. Whichever was installed last is the one used.",
                mods[0].Name, mods[1].Name, files.Count, files[0]), mods.Select(m => m.Name).ToList()));
        }

        // 3. A plugin needs another plugin that isn't installed.
        var guids = plugins.Select(p => p.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins)
            foreach (var dep in p.HardDependencies.Where(d => !guids.Contains(d) && !d.StartsWith("com.bepis.", StringComparison.OrdinalIgnoreCase)))
                list.Add(new Conflict(L.F("{0} needs another plugin ({1}) that isn't installed or is disabled.", p.Name, dep),
                    new() { OwnerName(game, manifest, p.File) ?? p.Name }));
        return list;
    }

    private static bool IsHarmless(string rel) =>
        rel.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || rel.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase);

    private static string? OwnerName(GameInstall game, Manifest manifest, string file)
    {
        var rel = Path.GetRelativePath(game.Root, file).Replace('\\', '/').Replace(".dll.disabled", ".dll");
        return manifest.Mods.FirstOrDefault(m => m.Files.Any(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase)))?.Name;
    }
}
