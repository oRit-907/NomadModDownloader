using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NomadModDownloader.Core;

/// <summary>A BepInEx plugin found in a DLL: its [BepInPlugin] id, name and version, plus its [BepInDependency]s.</summary>
public sealed record PluginInfo(string Guid, string Name, string Version, string File, List<string> HardDependencies);

/// <summary>
/// Reads [BepInPlugin] attributes straight from a DLL's metadata, without loading it,
/// so the app can tell which plugin is which (for logs, conflicts and config files).
/// </summary>
public static class PluginInspector
{
    public static List<PluginInfo> Read(string dll)
    {
        var found = new List<PluginInfo>();
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return found;
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(th);
                PluginInfo? plugin = null;
                var deps = new List<string>();
                foreach (var ah in type.GetCustomAttributes())
                {
                    var attr = md.GetCustomAttribute(ah);
                    var (name, paramTypes) = CtorInfo(md, attr.Constructor);
                    if (name is not ("BepInPlugin" or "BepInPluginAttribute" or "BepInDependency" or "BepInDependencyAttribute")) continue;
                    var blob = md.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1) continue; // prolog
                    var args = new List<object?>();
                    foreach (var p in paramTypes)
                    {
                        if (p == SignatureTypeCode.String) args.Add(blob.ReadSerializedString());
                        else if (blob.RemainingBytes >= 4) args.Add(blob.ReadInt32());
                    }
                    if (name.StartsWith("BepInPlugin") && args.Count >= 3)
                        plugin = new PluginInfo(args[0] as string ?? "", args[1] as string ?? "", args[2] as string ?? "", dll, deps);
                    else if (name.StartsWith("BepInDependency") && args.Count >= 1 && args[0] is string guid)
                    {
                        // (guid) and (guid, version) are hard dependencies; (guid, flags) is soft when flags has SoftDependency (2).
                        var soft = args.Count >= 2 && args[1] is int flags && (flags & 2) != 0;
                        if (!soft) deps.Add(guid);
                    }
                }
                if (plugin != null && plugin.Guid.Length > 0) found.Add(plugin);
            }
        }
        catch { /* not a .NET assembly, or locked: nothing to report */ }
        return found;
    }

    /// <summary>Every plugin under BepInEx\plugins (enabled ones only unless asked).</summary>
    public static List<PluginInfo> Scan(GameInstall game, bool includeDisabled = false)
    {
        var list = new List<PluginInfo>();
        if (!Directory.Exists(game.PluginsDir)) return list;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(game.PluginsDir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return list; }
        foreach (var f in files)
        {
            var isDll = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var isOff = f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);
            if (isDll || (includeDisabled && isOff)) list.AddRange(Read(f));
        }
        return list;
    }

    private static (string Name, List<SignatureTypeCode> Params) CtorInfo(MetadataReader md, EntityHandle ctor)
    {
        string name = "";
        BlobHandle sig = default;
        if (ctor.Kind == HandleKind.MemberReference)
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)ctor);
            sig = mr.Signature;
            if (mr.Parent.Kind == HandleKind.TypeReference) name = md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            else if (mr.Parent.Kind == HandleKind.TypeDefinition) name = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent).Name);
        }
        else if (ctor.Kind == HandleKind.MethodDefinition)
        {
            var m = md.GetMethodDefinition((MethodDefinitionHandle)ctor);
            sig = m.Signature;
            name = md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name);
        }
        var types = new List<SignatureTypeCode>();
        if (sig.IsNil) return (name, types);
        var r = md.GetBlobReader(sig);
        r.ReadSignatureHeader();
        var count = r.ReadCompressedInteger();
        r.ReadSignatureTypeCode(); // return type (void)
        for (var i = 0; i < count; i++)
        {
            var code = r.ReadSignatureTypeCode();
            types.Add(code);
            if (code is SignatureTypeCode.TypeHandle) r.ReadTypeHandle(); // enum (DependencyFlags)
        }
        return (name, types);
    }
}
