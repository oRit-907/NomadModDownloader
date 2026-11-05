using System.Buffers.Binary;
using System.Text;
using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Dxt;

namespace NomadModDownloader.Core;

/// <summary>A decoded texture: 32-bit BGRA pixels, top row first.</summary>
public sealed record DecodedTexture(string Name, int Width, int Height, byte[] Bgra);

/// <summary>
/// Just enough of Unity's serialized-file format to pull named Texture2D images out of a
/// game's .assets file (Unity 2019–6, no type trees needed). Used to show the player's own
/// copy of the game's menu art, so none of it has to ship inside this app.
/// </summary>
public static class UnityTextures
{
    private const int Texture2DClassId = 28;

    public static List<DecodedTexture> Read(string assetsPath, IReadOnlyCollection<string> names)
    {
        var found = new List<DecodedTexture>();
        var bytes = File.ReadAllBytes(assetsPath);
        foreach (var (name, obj) in Texture2DObjects(bytes))
        {
            if (!names.Contains(name)) continue;
            try
            {
                var tex = Decode(name, obj, Path.GetDirectoryName(assetsPath)!);
                if (tex != null) found.Add(tex);
            }
            catch { /* one unreadable texture shouldn't stop the rest */ }
        }
        return found;
    }

    /// <summary>Yields (name, raw object bytes) for every Texture2D in the file.</summary>
    internal static IEnumerable<(string Name, byte[] Data)> Texture2DObjects(byte[] b)
    {
        if (b.Length < 48) yield break;
        var version = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
        if (version < 17 || version > 30) yield break;
        long dataOffset;
        int o;
        if (version >= 22) { dataOffset = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(32)); o = 48; }
        else { dataOffset = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12)); o = 20; }
        var little = b[16] == 0;

        int I32(int at) => little ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at));
        long I64(int at) => little ? BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(at));

        var nul = Array.IndexOf(b, (byte)0, o);
        if (nul < 0) yield break;
        o = nul + 1;           // Unity version string
        o += 4;                // target platform
        var typeTrees = b[o++] != 0;
        if (typeTrees) yield break; // builds strip them; not worth supporting here
        var typeCount = I32(o); o += 4;
        if (typeCount is < 0 or > 100000) yield break;
        var classIds = new int[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            var cid = I32(o); o += 4;
            o += 1 + 2;        // isStripped, scriptTypeIndex
            if (cid == 114) o += 16; // script id
            o += 16;           // type hash
            classIds[i] = cid;
        }
        var objectCount = I32(o); o += 4;
        if (objectCount is < 0 or > 10_000_000) yield break;
        o = (o + 3) & ~3;
        for (var i = 0; i < objectCount; i++)
        {
            o += 8; // path id
            long start;
            if (version >= 22) { start = I64(o); o += 8; } else { start = (uint)I32(o); o += 4; }
            var size = I32(o); o += 4;
            var typeIndex = I32(o); o += 4;
            if (typeIndex < 0 || typeIndex >= classIds.Length || classIds[typeIndex] != Texture2DClassId) continue;
            var at = dataOffset + start;
            if (at < 0 || size <= 0 || at + size > b.Length) continue;
            var obj = b.AsSpan((int)at, size).ToArray();
            if (!little) continue; // Windows builds are always little-endian
            var len = BitConverter.ToInt32(obj, 0);
            if (len < 0 || len > 256 || 4 + len > obj.Length) continue;
            yield return (Encoding.UTF8.GetString(obj, 4, len), obj);
        }
    }

    private static DecodedTexture? Decode(string name, byte[] obj, string folder)
    {
        var q = (4 + BitConverter.ToInt32(obj, 0) + 3) & ~3;
        // m_ForcedFallbackFormat, m_Width, m_Height, m_CompleteImageSize, m_MipsStripped, m_TextureFormat
        var width = BitConverter.ToInt32(obj, q + 4);
        var height = BitConverter.ToInt32(obj, q + 8);
        var format = BitConverter.ToInt32(obj, q + 20);
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192) return null;

        // The object ends with m_StreamData { UInt64 offset; UInt32 size; string path }.
        byte[]? data = null;
        for (var p = obj.Length - 5; p >= q; p--)
        {
            var len = BitConverter.ToInt32(obj, p);
            if (len <= 0 || len > 260 || p + 4 + len > obj.Length) continue;
            var end = (p + 4 + len + 3) & ~3;
            if (end != obj.Length) continue;
            var path = Encoding.UTF8.GetString(obj, p + 4, len);
            if (!path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)) continue;
            var offset = BitConverter.ToInt64(obj, p - 12);
            var size = BitConverter.ToUInt32(obj, p - 4);
            var file = Path.Combine(folder, Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(file)) return null;
            using var fs = File.OpenRead(file);
            if (offset < 0 || offset + size > fs.Length) return null;
            fs.Position = offset;
            data = new byte[size];
            fs.ReadExactly(data);
            break;
        }
        if (data == null)
        {
            // Not streamed: image data is inline as int length + bytes just before an empty m_StreamData.
            // Empty stream data = offset 0 (8), size 0 (4), empty path (4) → 16 bytes at the end.
            var lenAt = obj.Length - 16 - 4;
            for (var p = q + 24; p < lenAt; p++)
            {
                var len = BitConverter.ToInt32(obj, p);
                if (len > 0 && p + 4 + len <= obj.Length && ((p + 4 + len + 3) & ~3) == obj.Length - 16)
                { data = obj.AsSpan(p + 4, len).ToArray(); break; }
            }
            if (data == null) return null;
        }

        byte[] bgra;
        switch (format)
        {
            case 25: // BC7
                Bc7.Decompress(data, width, height, out bgra);
                break;
            case 12: // DXT5
                DxtDecoder.DecompressDXT5(data, width, height, out bgra);
                break;
            case 10: // DXT1
                DxtDecoder.DecompressDXT1(data, width, height, out bgra);
                break;
            case 4: // RGBA32
                bgra = new byte[width * height * 4];
                for (var i = 0; i < bgra.Length && i + 3 < data.Length; i += 4)
                { bgra[i] = data[i + 2]; bgra[i + 1] = data[i + 1]; bgra[i + 2] = data[i]; bgra[i + 3] = data[i + 3]; }
                break;
            default:
                return null; // crunched or unusual formats aren't decoded
        }
        if (bgra.Length < width * height * 4) return null;
        FlipRows(bgra, width, height); // Unity stores the bottom row first
        return new DecodedTexture(name, width, height, bgra);
    }

    private static void FlipRows(byte[] px, int w, int h)
    {
        var stride = w * 4;
        var tmp = new byte[stride];
        for (int top = 0, bottom = h - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(px, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(px, bottom * stride, px, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, px, bottom * stride, stride);
        }
    }
}
