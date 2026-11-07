using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace NomadModManager
{
    /// <summary>A saved set of mods: which ones are on. Mods not in it are switched off when it's used.</summary>
    internal sealed class Profile
    {
        public string Name = "";
        public DateTime Saved;
        public List<ProfileMod> On = new List<ProfileMod>();
    }

    internal sealed class ProfileMod
    {
        public string Rel = "";    // BepInEx/plugins/<entry>
        public string Name = "";
        public int NexusId;        // to install it again if it's gone
    }

    /// <summary>Profiles kept next to the game (BepInEx\NomadModManager\profiles.json).</summary>
    internal static class Profiles
    {
        private static string FilePath => Path.Combine(Pending.Dir(BepInEx.Paths.GameRootPath), "profiles.json");

        public static List<Profile> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Profile>();
                return JsonConvert.DeserializeObject<List<Profile>>(File.ReadAllText(FilePath)) ?? new List<Profile>();
            }
            catch { return new List<Profile>(); }
        }

        public static void Save(List<Profile> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Couldn't save profiles: " + e.Message); }
        }

        public static bool IsOn(ModEntry m) =>
            m.State != ModState.Disabled && m.State != ModState.WillDisable && m.State != ModState.WillRemove;

        /// <summary>Saves what's on right now (replacing a profile with the same name).</summary>
        public static Profile Capture(string name, ModCatalog cat)
        {
            var list = Load();
            list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var p2 = new Profile
            {
                Name = name, Saved = DateTime.Now,
                On = cat.Mods.Where(m => !m.IsSelf && IsOn(m)).Select(m => new ProfileMod { Rel = m.Rel, Name = m.Name, NexusId = m.NexusId }).ToList(),
            };
            list.Add(p2);
            Save(list);
            return p2;
        }

        public static void Delete(string name)
        {
            var list = Load();
            list.RemoveAll(p => p.Name == name);
            Save(list);
        }

        /// <summary>Switches mods on and off to match. Returns how many changed, and the profile's mods that aren't installed any more.</summary>
        public static (int Changed, List<ProfileMod> Missing) Apply(Profile p, ModCatalog cat)
        {
            var changed = 0;
            var want = new HashSet<string>(p.On.Select(x => x.Rel), StringComparer.OrdinalIgnoreCase);
            foreach (var m in cat.Mods.ToList())
            {
                if (m.IsSelf || m.State == ModState.WillRemove) continue;
                var on = IsOn(m);
                if (want.Contains(m.Rel) && !on) { cat.Enable(m); changed++; }
                else if (!want.Contains(m.Rel) && on && m.State != ModState.Library) { cat.Disable(m); changed++; }
            }
            var missing = p.On.Where(x => !cat.Mods.Any(m => m.Rel.Equals(x.Rel, StringComparison.OrdinalIgnoreCase))).ToList();
            return (changed, missing);
        }
    }
}
