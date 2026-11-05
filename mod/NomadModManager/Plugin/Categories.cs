using System.Text.RegularExpressions;

namespace NomadModManager
{
    /// <summary>
    /// Nexus files every Nomad Drive mod under "Miscellaneous", so the Mods menu sorts them itself by what they do,
    /// from the mod's name first and then its summary. The first category that matches wins.
    /// </summary>
    internal static class Categories
    {
        public const string Misc = "Miscellaneous";

        private static readonly (string Name, Regex Match)[] Rules =
        {
            ("Tools & Frameworks", R(@"bepinex|downloader|mod ?sync|vortex|framework|\bsdk\b|mod menu|mod manager|modmenu|loader|\bfixes\b")),
            ("Cheats", R(@"cheat|trainer|spawner|spawn menu|god ?mode|\bfly|noclip|infinite|飞行")),
            ("Multiplayer", R(@"multiplayer|co-?op|more ?players|lobby|playername|player name|together|玩家")),
            ("HUD & Interface", R(@"\bhud\b|\bmap\b|minimap|compass|overlay|\bui\b|interface|highlight|hotbar|dashboard|odometer|menu")),
            ("Vehicles & Driving", R(@"vehicle|\brv\b|\bcar\b|\bvan\b|\bbus\b|gearbox|cruise|\btow\b|autopilot|steering|\bwheel|pedal|shifter|engine|turbo|headlight|driving|自动驾驶")),
            ("Building & Placement", R(@"placement|place any|place every|\bplace\b|build|restoration|spray|paint|polish|摆放|喷漆|打磨|抛光")),
            ("Gameplay", R(@"wildlife|animal|zombie|weapon|rifle|biome|realism|weather|market|survival|loot|hunger|thirst|sleep|world|economy|coins|chest|宝箱")),
            ("Items & Decor", R(@"\bitems?\b|lamp|light\b|pillow|plush|blahaj|blåhaj|boombox|radio|music|furniture|decor|poster|neon|cigar|e-cig|sink|shower|toilet|water tank|wardrobe|outfit|cloth")),
            ("Graphics & Performance", R(@"graphic|lighting|reflection|performance|fps|texture|shader|visual")),
        };

        /// <summary>All categories in display order, Miscellaneous last.</summary>
        public static readonly string[] Order;

        static Categories()
        {
            Order = new string[Rules.Length + 1];
            for (var i = 0; i < Rules.Length; i++) Order[i] = Rules[i].Name;
            Order[Rules.Length] = Misc;
        }

        private static Regex R(string pattern) => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Classify(string name, string summary)
        {
            foreach (var text in new[] { name, summary })
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var (cat, rx) in Rules)
                    if (rx.IsMatch(text)) return cat;
            }
            return Misc;
        }

        public static int Rank(string category)
        {
            for (var i = 0; i < Order.Length; i++) if (Order[i] == category) return i;
            return Order.Length;
        }
    }
}
