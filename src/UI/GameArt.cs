using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NomadModDownloader.Core;

namespace NomadModDownloader.UI;

/// <summary>
/// The game's main-menu backgrounds and title logo, read from the player's own Nomad Drive
/// install (none of it ships in this app). Decoded once, then cached as small JPG/PNG files.
/// </summary>
public static class GameArt
{
    private static readonly string[] Backgrounds = { "MainMenuBackground-01", "MainMenuBackground-02", "MainMenuBackground-03", "MainMenuBackground-04" };

    private static string Dir => Path.Combine(AppPaths.Root, "art");

    /// <summary>Background image paths (may be empty) and a logo path (may be null).</summary>
    public static (List<string> Backgrounds, string? Logo) Load(GameInstall game)
    {
        var bgs = new List<string>();
        try
        {
            Directory.CreateDirectory(Dir);
            var assets = Path.Combine(game.DataFolder, "sharedassets1.assets");
            var stampFile = Path.Combine(Dir, "source.txt");
            string stamp = "";
            if (File.Exists(assets))
            {
                var fi = new FileInfo(assets);
                stamp = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|1";
            }
            var cached = Enumerable.Range(1, 4).Select(i => Path.Combine(Dir, $"bg{i}.jpg")).Where(File.Exists).ToList();
            if (stamp.Length > 0 && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp && cached.Count > 0)
                bgs = cached;
            else if (stamp.Length > 0)
            {
                foreach (var old in Directory.EnumerateFiles(Dir, "bg*.jpg")) File.Delete(old);
                var textures = UnityTextures.Read(assets, Backgrounds).OrderBy(t => t.Name).ToList();
                var n = 0;
                foreach (var t in textures)
                {
                    var path = Path.Combine(Dir, $"bg{++n}.jpg");
                    SaveJpg(t, path, maxWidth: 1600);
                    bgs.Add(path);
                }
                File.WriteAllText(stampFile, stamp);
            }
        }
        catch { /* no art is fine: the plain dark backdrop shows instead */ }

        // The in-game title logo is crunch-compressed, so use Steam's cached copy of the game's logo when there is one.
        string? logo = null;
        try { logo = GameLocator.SteamLibraryImage("logo.png"); } catch { }
        return (bgs, logo);
    }

    public static Image? Open(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    private static void SaveJpg(DecodedTexture t, string path, int maxWidth)
    {
        using var full = new Bitmap(t.Width, t.Height, PixelFormat.Format32bppArgb);
        var data = full.LockBits(new Rectangle(0, 0, t.Width, t.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < t.Height; y++)
                Marshal.Copy(t.Bgra, y * t.Width * 4, data.Scan0 + y * data.Stride, t.Width * 4);
        }
        finally { full.UnlockBits(data); }

        var w = Math.Min(maxWidth, t.Width);
        var h = (int)Math.Round(t.Height * (w / (double)t.Width));
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, w, h);
        }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        small.Save(path, jpeg, p);
    }
}
