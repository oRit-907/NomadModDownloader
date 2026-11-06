using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NomadModDownloader.Core;

/// <summary>
/// Finds the game methods, fields and types a mod uses that the game's own files don't have. Nomad Drive is built
/// with Unity's code stripping, so many Unity methods mods expect (AssetBundle.LoadFromFile, ...) are missing.
/// A mod that calls one fails with MissingMethodException, often only when that code runs.
/// </summary>
public static class GameApiScanner
{
    public sealed record Missing(string Assembly, string Type, string Member, bool IsType)
    {
        public string Display => IsType ? Type : $"{Type}.{Member}";
        public bool IsUnity => Assembly.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the game's Managed folder contains, read once and reused for every mod.</summary>
    public sealed class GameApi : IDisposable
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Asm?> _asms = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="dirs">Folders to read, lowest priority first: a file in a later folder replaces the same file in an earlier one.</param>
        public GameApi(params string[] dirs)
        {
            foreach (var d in dirs)
                if (Directory.Exists(d))
                    foreach (var f in Directory.EnumerateFiles(d, "*.dll"))
                        _files[Path.GetFileNameWithoutExtension(f)] = f;
        }

        /// <summary>Only the game's own (stripped) assemblies are checked; .NET libraries come from the .NET fix.</summary>
        public bool Checks(string asm) =>
            _files.ContainsKey(asm) && !IsFramework(asm);

        internal Asm? Get(string name)
        {
            if (_asms.TryGetValue(name, out var a)) return a;
            a = _files.TryGetValue(name, out var f) ? Asm.TryOpen(f) : null;
            _asms[name] = a;
            return a;
        }

        public void Dispose()
        {
            foreach (var a in _asms.Values) a?.Dispose();
            _asms.Clear();
        }
    }

    internal static bool IsFramework(string asm) =>
        asm is "mscorlib" or "netstandard" or "System" || asm.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || asm.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) || asm.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);

    /// <summary>A loaded game assembly: its types by full name, and each type's members as signature keys.</summary>
    internal sealed class Asm : IDisposable
    {
        private readonly PEReader _pe;
        public readonly MetadataReader Md;
        public readonly Dictionary<string, TypeDefinitionHandle> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Forwards = new(StringComparer.Ordinal);
        private readonly Dictionary<TypeDefinitionHandle, HashSet<string>> _members = new();

        private Asm(PEReader pe)
        {
            _pe = pe;
            Md = pe.GetMetadataReader();
            foreach (var h in Md.TypeDefinitions) Types[FullName(Md, h)] = h;
            foreach (var h in Md.ExportedTypes)
            {
                var e = Md.GetExportedType(h);
                if (!e.IsForwarder || e.Implementation.Kind != HandleKind.AssemblyReference) continue;
                var ns = Md.GetString(e.Namespace);
                var n = (ns.Length > 0 ? ns + "." : "") + Md.GetString(e.Name);
                Forwards[n] = Md.GetString(Md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name);
            }
        }

        public static Asm? TryOpen(string file)
        {
            try
            {
                // Read into memory: the game's files stay unlocked (Steam updates them).
                var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(file)));
                if (!pe.HasMetadata) { pe.Dispose(); return null; }
                return new Asm(pe);
            }
            catch { return null; }
        }

        public HashSet<string> Members(TypeDefinitionHandle h)
        {
            if (_members.TryGetValue(h, out var set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            var t = Md.GetTypeDefinition(h);
            var p = new SigNames(Md);
            foreach (var mh in t.GetMethods())
            {
                var m = Md.GetMethodDefinition(mh);
                try { set.Add(MethodKey(Md.GetString(m.Name), m.DecodeSignature(p, null))); } catch { }
            }
            foreach (var fh in t.GetFields())
            {
                var f = Md.GetFieldDefinition(fh);
                try { set.Add(FieldKey(Md.GetString(f.Name), f.DecodeSignature(p, null))); } catch { }
            }
            _members[h] = set;
            return set;
        }

        /// <summary>The base type's full name and assembly, when it's a plain type reference or definition.</summary>
        public (string? asm, string? type) BaseOf(TypeDefinitionHandle h, string self)
        {
            var b = Md.GetTypeDefinition(h).BaseType;
            if (b.IsNil) return (null, null);
            if (b.Kind == HandleKind.TypeDefinition) return (self, FullName(Md, (TypeDefinitionHandle)b));
            if (b.Kind == HandleKind.TypeReference) { var (a, n) = RefName(Md, (TypeReferenceHandle)b); return (a ?? self, n); }
            return (null, null);
        }

        public void Dispose() => _pe.Dispose();
    }

    /// <summary>Lists what <paramref name="modDll"/> uses that the game doesn't have.</summary>
    public static List<Missing> Scan(string modDll, GameApi api)
    {
        var result = new List<Missing>();
        using var pe = new PEReader(File.OpenRead(modDll));
        if (!pe.HasMetadata) return result;
        var md = pe.GetMetadataReader();
        var p = new SigNames(md);
        var seen = new HashSet<string>();

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue; // generic instances/method specs: skip (rare in mods)
            var (asm, type) = RefName(md, (TypeReferenceHandle)mr.Parent);
            if (asm == null || type == null || !api.Checks(asm)) continue;
            var name = md.GetString(mr.Name);
            string key;
            try
            {
                key = mr.GetKind() == MemberReferenceKind.Method
                    ? MethodKey(name, mr.DecodeMethodSignature(p, null))
                    : FieldKey(name, mr.DecodeFieldSignature(p, null));
            }
            catch { continue; }
            if (!seen.Add(asm + "|" + type + "|" + key)) continue;
            var found = Find(api, asm, type, key, 0);
            if (found == Found.Yes) continue;
            result.Add(found == Found.NoType ? new Missing(asm, type, "", true) : new Missing(asm, type, Pretty(key), false));
        }
        // Types used without any member reference (casts, generic arguments, base classes).
        foreach (var h in md.TypeReferences)
        {
            var (asm, type) = RefName(md, h);
            if (asm == null || type == null || !api.Checks(asm)) continue;
            if (!seen.Add(asm + "|" + type + "|")) continue;
            if (Resolve(api, asm, type, 0) == null && !result.Any(r => r.IsType && r.Type == type))
                result.Add(new Missing(asm, type, "", true));
        }
        // A missing type also explains its missing members: keep just the type.
        var missingTypes = result.Where(r => r.IsType).Select(r => r.Type).ToHashSet();
        result.RemoveAll(r => !r.IsType && missingTypes.Contains(r.Type));
        return result.DistinctBy(r => r.Assembly + "|" + r.Display).ToList();
    }

    private enum Found { Yes, NoMember, NoType }

    private static Found Find(GameApi api, string asm, string type, string key, int depth)
    {
        var r = Resolve(api, asm, type, 0);
        if (r == null) return Found.NoType;
        var (a, h, an) = r.Value;
        if (a.Members(h).Contains(key)) return Found.Yes;
        // Members a C# compiler binds to the declared type may live on a base class (and .ctor never does).
        if (depth < 12 && !key.StartsWith(".ctor("))
        {
            var (ba, bt) = a.BaseOf(h, an);
            if (ba != null && bt != null && api.Checks(ba))
                return Find(api, ba, bt, key, depth + 1) == Found.Yes ? Found.Yes : Found.NoMember;
            // Base is a .NET type: only System.Object's own members can come from there.
            if (ba != null && bt != null) return ObjectMembers.Contains(key) ? Found.Yes : Found.NoMember;
        }
        return Found.NoMember;
    }

    private static readonly HashSet<string> ObjectMembers = new(StringComparer.Ordinal)
    {
        "ToString()String", "GetHashCode()Int32", "Equals(Object)Boolean", "GetType()System.Type", "Finalize()Void", "MemberwiseClone()Object",
    };

    private static (Asm, TypeDefinitionHandle, string)? Resolve(GameApi api, string asm, string type, int depth)
    {
        var a = api.Get(asm);
        if (a == null) return null;
        if (a.Types.TryGetValue(type, out var h)) return (a, h, asm);
        if (depth < 4 && a.Forwards.TryGetValue(type.Split('/')[0], out var to)) return Resolve(api, to, type, depth + 1);
        return null;
    }

    private static (string? asm, string? name) RefName(MetadataReader md, TypeReferenceHandle h)
    {
        var t = md.GetTypeReference(h);
        var ns = md.GetString(t.Namespace);
        var n = (ns.Length > 0 ? ns + "." : "") + md.GetString(t.Name);
        switch (t.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                return (md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name), n);
            case HandleKind.TypeReference:
                var (a, outer) = RefName(md, (TypeReferenceHandle)t.ResolutionScope);
                return (a, outer + "/" + md.GetString(t.Name));
            default:
                return (null, null);
        }
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var name = md.GetString(t.Name);
        var decl = t.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, decl) + "/" + name;
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    private static string MethodKey(string name, MethodSignature<string> s) =>
        $"{name}{(s.GenericParameterCount > 0 ? "`" + s.GenericParameterCount : "")}({string.Join(",", s.ParameterTypes)}){s.ReturnType}"
        + (s.Header.IsInstance ? "" : "s");

    private static string FieldKey(string name, string type) => $"{name}:{type}";

    private static string Pretty(string key)
    {
        var i = key.IndexOf(')');
        return i > 0 ? key[..(i + 1)] : key.Split(':')[0];
    }

    /// <summary>Turns signature types into assembly-free names, so a mod's view and the game's view compare equal.</summary>
    private sealed class SigNames : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _md;
        public SigNames(MetadataReader md) => _md = md;
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => FullName(r, h);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => RefName(r, h).name ?? "?";
        public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
            r.GetTypeSpecification(h).DecodeSignature(this, c);
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetPinnedType(string e) => e;
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericTypeParameter(object? c, int i) => "!" + i;
        public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetModifiedType(string m, string u, bool req) => u;
    }
}
