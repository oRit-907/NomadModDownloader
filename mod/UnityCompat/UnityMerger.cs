using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NomadUnityCompat
{
    /// <summary>
    /// Adds what a stripped Unity module is missing (types, fields, methods, properties, events) from the full module of
    /// the same Unity version. Only adds: everything the game already has stays exactly as the game built it.
    /// </summary>
    public sealed class UnityMerger
    {
        private readonly ModuleDefinition _dst, _src;
        private readonly Dictionary<string, TypeDefinition> _types = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        private readonly HashSet<TypeDefinition> _newTypes = new HashSet<TypeDefinition>();
        private readonly Dictionary<MethodDefinition, MethodDefinition> _clonedMethods = new Dictionary<MethodDefinition, MethodDefinition>();
        private readonly HashSet<FieldDefinition> _newStaticFields = new HashSet<FieldDefinition>();
        private readonly List<(FieldDefinition src, FieldDefinition dst)> _addedFields = new List<(FieldDefinition, FieldDefinition)>();

        public int TypesAdded, MethodsAdded, FieldsAdded, PropertiesAdded, EventsAdded, ForwardersAdded;
        public readonly List<string> Warnings = new List<string>();

        public int Total => TypesAdded + MethodsAdded + FieldsAdded + PropertiesAdded + EventsAdded + ForwardersAdded;

        private UnityMerger(ModuleDefinition dst, ModuleDefinition src)
        {
            _dst = dst;
            _src = src;
        }

        /// <summary>Merges <paramref name="full"/> into <paramref name="game"/> in place.</summary>
        public static UnityMerger Merge(AssemblyDefinition game, AssemblyDefinition full)
        {
            var m = new UnityMerger(game.MainModule, full.MainModule);
            m.Run();
            return m;
        }

        private void Run()
        {
            foreach (var t in AllTypes(_dst.Types)) _types[t.FullName] = t;

            // 1. Type shells, outer types first, so every later reference can be mapped.
            var srcTypes = AllTypes(_src.Types).ToList();
            foreach (var s in srcTypes)
            {
                if (s.Name == "<Module>" || _types.ContainsKey(s.FullName)) continue;
                var d = new TypeDefinition(s.Namespace, s.Name, s.Attributes);
                if (s.IsNested)
                {
                    if (!_types.TryGetValue(s.DeclaringType.FullName, out var outer)) continue;
                    outer.NestedTypes.Add(d);
                }
                else _dst.Types.Add(d);
                foreach (var gp in s.GenericParameters) d.GenericParameters.Add(new GenericParameter(gp.Name, d) { Attributes = gp.Attributes });
                _types[s.FullName] = d;
                _newTypes.Add(d);
                TypesAdded++;
            }

            // 2. Shape of the new types.
            foreach (var s in srcTypes)
            {
                if (!_types.TryGetValue(s.FullName, out var d) || !_newTypes.Contains(d)) continue;
                if (s.BaseType != null) d.BaseType = MapType(s.BaseType);
                d.PackingSize = s.PackingSize;
                d.ClassSize = s.ClassSize;
                CopyGenericConstraints(s.GenericParameters, d.GenericParameters);
            }

            // 3. Fields and method shells (signatures only).
            var bodies = new List<(MethodDefinition src, MethodDefinition dst)>();
            foreach (var s in srcTypes)
            {
                if (!_types.TryGetValue(s.FullName, out var d)) continue;
                var fields = new HashSet<string>(d.Fields.Select(f => f.Name), StringComparer.Ordinal);
                foreach (var sf in s.Fields)
                {
                    if (fields.Contains(sf.Name)) continue;
                    var df = new FieldDefinition(sf.Name, sf.Attributes, MapType(sf.FieldType));
                    if (sf.HasConstant) df.Constant = sf.Constant;
                    if (sf.InitialValue != null && sf.InitialValue.Length > 0) df.InitialValue = sf.InitialValue;
                    if (sf.HasLayoutInfo) df.Offset = sf.Offset;
                    if (sf.HasMarshalInfo) df.MarshalInfo = sf.MarshalInfo;
                    d.Fields.Add(df);
                    _addedFields.Add((sf, df));
                    if (df.IsStatic && !df.IsLiteral) _newStaticFields.Add(df);
                    if (!_newTypes.Contains(d)) FieldsAdded++;
                }

                var methods = new HashSet<string>(d.Methods.Select(MethodKey), StringComparer.Ordinal);
                foreach (var sm in s.Methods)
                {
                    if (methods.Contains(MethodKey(sm))) continue;
                    if (sm.IsConstructor && sm.IsStatic && d.Methods.Any(x => x.IsConstructor && x.IsStatic)) continue; // handled below
                    var dm = CloneMethodShell(sm, d);
                    d.Methods.Add(dm);
                    _clonedMethods[sm] = dm;
                    bodies.Add((sm, dm));
                    if (!_newTypes.Contains(d)) MethodsAdded++;
                }
            }

            // 4. Interfaces, overrides, properties, events, attributes (need every method to exist).
            foreach (var s in srcTypes)
            {
                if (!_types.TryGetValue(s.FullName, out var d)) continue;
                var ifaces = new HashSet<string>(d.Interfaces.Select(i => TypeKey(i.InterfaceType)), StringComparer.Ordinal);
                foreach (var si in s.Interfaces)
                    if (ifaces.Add(TypeKey(si.InterfaceType)))
                        d.Interfaces.Add(new InterfaceImplementation(MapType(si.InterfaceType)));

                var props = new HashSet<string>(d.Properties.Select(p => p.Name), StringComparer.Ordinal);
                foreach (var sp in s.Properties)
                {
                    if (props.Contains(sp.Name)) continue;
                    var dp = new PropertyDefinition(sp.Name, sp.Attributes, MapType(sp.PropertyType))
                    {
                        GetMethod = sp.GetMethod != null ? FindMethod(d, sp.GetMethod) : null,
                        SetMethod = sp.SetMethod != null ? FindMethod(d, sp.SetMethod) : null,
                    };
                    if (sp.HasConstant) dp.Constant = sp.Constant;
                    d.Properties.Add(dp);
                    CopyAttributes(sp, dp);
                    if (!_newTypes.Contains(d)) PropertiesAdded++;
                }

                var events = new HashSet<string>(d.Events.Select(e => e.Name), StringComparer.Ordinal);
                foreach (var se in s.Events)
                {
                    if (events.Contains(se.Name)) continue;
                    var de = new EventDefinition(se.Name, se.Attributes, MapType(se.EventType))
                    {
                        AddMethod = se.AddMethod != null ? FindMethod(d, se.AddMethod) : null,
                        RemoveMethod = se.RemoveMethod != null ? FindMethod(d, se.RemoveMethod) : null,
                        InvokeMethod = se.InvokeMethod != null ? FindMethod(d, se.InvokeMethod) : null,
                    };
                    d.Events.Add(de);
                    CopyAttributes(se, de);
                    if (!_newTypes.Contains(d)) EventsAdded++;
                }

                if (_newTypes.Contains(d)) CopyAttributes(s, d);
            }
            foreach (var (sf, df) in _addedFields) CopyAttributes(sf, df);

            foreach (var (sm, dm) in bodies)
            {
                foreach (var o in sm.Overrides)
                {
                    try { dm.Overrides.Add(MapMethod(o)); }
                    catch (Exception e) { Warnings.Add($"override {sm.FullName}: {e.Message}"); }
                }
                CopyAttributes(sm, dm);
                CopyAttributes(sm.MethodReturnType, dm.MethodReturnType);
                for (var i = 0; i < sm.Parameters.Count; i++) CopyAttributes(sm.Parameters[i], dm.Parameters[i]);
            }

            // 5. Bodies.
            foreach (var (sm, dm) in bodies)
            {
                if (!sm.HasBody) continue;
                try { CopyBody(sm, dm); }
                catch (Exception e)
                {
                    Warnings.Add($"body {sm.FullName}: {e.Message}");
                    dm.Body = new MethodBody(dm);
                    ThrowBody(dm, "Nomad Unity Compatibility couldn't restore " + sm.FullName);
                }
            }

            // 6. Static fields the game stripped along with their initializers.
            foreach (var s in srcTypes)
            {
                if (!_types.TryGetValue(s.FullName, out var d) || _newTypes.Contains(d)) continue;
                var srcCctor = s.Methods.FirstOrDefault(x => x.IsConstructor && x.IsStatic);
                var dstCctor = d.Methods.FirstOrDefault(x => x.IsConstructor && x.IsStatic);
                if (srcCctor == null || dstCctor == null || _clonedMethods.ContainsKey(srcCctor)) continue;
                if (!d.Fields.Any(f => _newStaticFields.Contains(f))) continue;
                try { AddPartialCctor(srcCctor, dstCctor, d); }
                catch (Exception e) { Warnings.Add($"static init {s.FullName}: {e.Message}"); }
            }

            // 7. Type forwarders (UnityEngine.dll forwards to the modules).
            var fwd = new HashSet<string>(_dst.ExportedTypes.Select(e => e.FullName), StringComparer.Ordinal);
            foreach (var e in _src.ExportedTypes)
            {
                if (!e.IsForwarder || fwd.Contains(e.FullName) || !(e.Scope is AssemblyNameReference an)) continue;
                var scope = _dst.AssemblyReferences.FirstOrDefault(a => a.Name == an.Name);
                if (scope == null) { scope = new AssemblyNameReference(an.Name, an.Version); _dst.AssemblyReferences.Add(scope); }
                _dst.ExportedTypes.Add(new ExportedType(e.Namespace, e.Name, _dst, scope) { Attributes = e.Attributes });
                ForwardersAdded++;
            }
        }

        // ---------- members ----------

        private MethodDefinition CloneMethodShell(MethodDefinition s, TypeDefinition owner)
        {
            var d = new MethodDefinition(s.Name, s.Attributes, _dst.TypeSystem.Void)
            {
                ImplAttributes = s.ImplAttributes,
                HasThis = s.HasThis,
                ExplicitThis = s.ExplicitThis,
                CallingConvention = s.CallingConvention,
                SemanticsAttributes = s.SemanticsAttributes,
            };
            foreach (var gp in s.GenericParameters) d.GenericParameters.Add(new GenericParameter(gp.Name, d) { Attributes = gp.Attributes });
            d.ReturnType = MapType(s.ReturnType);
            foreach (var p in s.Parameters)
            {
                var dp = new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType));
                if (p.HasConstant) dp.Constant = p.Constant;
                if (p.HasMarshalInfo) dp.MarshalInfo = p.MarshalInfo;
                d.Parameters.Add(dp);
            }
            if (s.MethodReturnType.HasMarshalInfo) d.MethodReturnType.MarshalInfo = s.MethodReturnType.MarshalInfo;
            if (s.HasPInvokeInfo)
            {
                var mr = _dst.ModuleReferences.FirstOrDefault(x => x.Name == s.PInvokeInfo.Module.Name);
                if (mr == null) { mr = new ModuleReference(s.PInvokeInfo.Module.Name); _dst.ModuleReferences.Add(mr); }
                d.PInvokeInfo = new PInvokeInfo(s.PInvokeInfo.Attributes, s.PInvokeInfo.EntryPoint, mr);
            }
            CopyGenericConstraints(s.GenericParameters, d.GenericParameters);
            return d;
        }

        private void CopyGenericConstraints(IList<GenericParameter> src, IList<GenericParameter> dst)
        {
            for (var i = 0; i < src.Count && i < dst.Count; i++)
                foreach (var c in src[i].Constraints)
                    dst[i].Constraints.Add(MapType(c));
        }

        private MethodDefinition FindMethod(TypeDefinition d, MethodDefinition s)
        {
            if (_clonedMethods.TryGetValue(s, out var c)) return c;
            var key = MethodKey(s);
            return d.Methods.FirstOrDefault(m => MethodKey(m) == key);
        }

        private void CopyBody(MethodDefinition s, MethodDefinition d)
        {
            var sb = s.Body;
            var db = d.Body;
            db.InitLocals = sb.InitLocals;
            foreach (var v in sb.Variables) db.Variables.Add(new VariableDefinition(MapType(v.VariableType)));
            var map = new Dictionary<Instruction, Instruction>();
            var il = new List<Instruction>();
            foreach (var i in sb.Instructions)
            {
                var n = Instruction.Create(OpCodes.Nop);
                n.OpCode = i.OpCode;
                map[i] = n;
                il.Add(n);
            }
            for (var k = 0; k < il.Count; k++)
                il[k].Operand = MapOperand(sb.Instructions[k].Operand, sb, db, map);
            foreach (var n in il) db.Instructions.Add(n);
            foreach (var h in sb.ExceptionHandlers)
                db.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                {
                    TryStart = Get(map, h.TryStart), TryEnd = Get(map, h.TryEnd),
                    HandlerStart = Get(map, h.HandlerStart), HandlerEnd = Get(map, h.HandlerEnd),
                    FilterStart = Get(map, h.FilterStart),
                    CatchType = h.CatchType != null ? MapType(h.CatchType) : null,
                });
        }

        private static Instruction Get(Dictionary<Instruction, Instruction> m, Instruction i) => i == null ? null : m[i];

        private object MapOperand(object o, MethodBody sb, MethodBody db, Dictionary<Instruction, Instruction> map)
        {
            switch (o)
            {
                case null: return null;
                case Instruction i: return map[i];
                case Instruction[] a: return a.Select(x => map[x]).ToArray();
                case VariableDefinition v: return db.Variables[v.Index];
                case ParameterDefinition p:
                    if (p == sb.ThisParameter) return db.ThisParameter;
                    return db.Method.Parameters[p.Index];
                case TypeReference t: return MapType(t);
                case MethodReference m: return MapMethod(m);
                case FieldReference f: return MapField(f);
                case CallSite cs:
                    var n = new CallSite(MapType(cs.ReturnType)) { HasThis = cs.HasThis, ExplicitThis = cs.ExplicitThis, CallingConvention = cs.CallingConvention };
                    foreach (var p in cs.Parameters) n.Parameters.Add(new ParameterDefinition(MapType(p.ParameterType)));
                    return n;
                default: return o; // numbers, strings
            }
        }

        /// <summary>Runs the full module's static initializer for the fields the game stripped, without touching the rest.</summary>
        private void AddPartialCctor(MethodDefinition srcCctor, MethodDefinition dstCctor, TypeDefinition d)
        {
            var init = new MethodDefinition("<NomadUnityCompat>cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, _dst.TypeSystem.Void);
            d.Methods.Add(init);
            CopyBody(srcCctor, init);
            // Taking the address of a field the game already set up would re-initialize it: leave those types alone.
            if (init.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsflda && i.Operand is FieldReference af
                    && af.DeclaringType.FullName == d.FullName && !d.Fields.Any(x => x.Name == af.Name && _newStaticFields.Contains(x))))
            {
                d.Methods.Remove(init);
                Warnings.Add("static init skipped for " + d.FullName);
                return;
            }
            // Stores into fields the game already initializes become pops.
            foreach (var i in init.Body.Instructions)
                if (i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.DeclaringType.FullName == d.FullName
                    && !d.Fields.Any(x => x.Name == f.Name && _newStaticFields.Contains(x)))
                {
                    i.OpCode = OpCodes.Pop;
                    i.Operand = null;
                }
            var ret = dstCctor.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList();
            var proc = dstCctor.Body.GetILProcessor();
            foreach (var r in ret) proc.InsertBefore(r, Instruction.Create(OpCodes.Call, init));
        }

        private void ThrowBody(MethodDefinition m, string msg)
        {
            var ctor = new MethodReference(".ctor", _dst.TypeSystem.Void,
                new TypeReference("System", "MissingMethodException", _dst, _dst.TypeSystem.CoreLibrary)) { HasThis = true };
            ctor.Parameters.Add(new ParameterDefinition(_dst.TypeSystem.String));
            var p = m.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldstr, msg));
            p.Append(Instruction.Create(OpCodes.Newobj, ctor));
            p.Append(Instruction.Create(OpCodes.Throw));
        }

        private void CopyAttributes(ICustomAttributeProvider s, ICustomAttributeProvider d)
        {
            if (!s.HasCustomAttributes) return;
            var have = new HashSet<string>(d.CustomAttributes.Select(a => a.AttributeType.FullName), StringComparer.Ordinal);
            foreach (var a in s.CustomAttributes)
            {
                if (have.Contains(a.AttributeType.FullName)) continue;
                try
                {
                    var n = new CustomAttribute(MapMethod(a.Constructor));
                    foreach (var arg in a.ConstructorArguments) n.ConstructorArguments.Add(MapArg(arg));
                    foreach (var f in a.Fields) n.Fields.Add(new CustomAttributeNamedArgument(f.Name, MapArg(f.Argument)));
                    foreach (var p in a.Properties) n.Properties.Add(new CustomAttributeNamedArgument(p.Name, MapArg(p.Argument)));
                    d.CustomAttributes.Add(n);
                }
                catch { /* attributes are metadata only */ }
            }
        }

        private CustomAttributeArgument MapArg(CustomAttributeArgument a)
        {
            object v = a.Value;
            if (v is TypeReference t) v = MapType(t);
            else if (v is CustomAttributeArgument inner) v = MapArg(inner);
            else if (v is CustomAttributeArgument[] arr) v = arr.Select(MapArg).ToArray();
            return new CustomAttributeArgument(MapType(a.Type), v);
        }

        // ---------- references ----------

        private bool IsLocal(TypeReference t) => t.Scope is ModuleDefinition md && md == _src
            || t is TypeDefinition td && td.Module == _src;

        private TypeReference MapType(TypeReference t)
        {
            switch (t)
            {
                case GenericParameter gp:
                    return Positional(gp.Position, gp.Type);
                case ArrayType a:
                    var at = new ArrayType(MapType(a.ElementType), a.Rank);
                    if (a.Rank > 1)
                    {
                        at.Dimensions.Clear();
                        foreach (var dim in a.Dimensions) at.Dimensions.Add(dim);
                    }
                    return at;
                case ByReferenceType b: return new ByReferenceType(MapType(b.ElementType));
                case PointerType p: return new PointerType(MapType(p.ElementType));
                case PinnedType p: return new PinnedType(MapType(p.ElementType));
                case RequiredModifierType r: return new RequiredModifierType(MapType(r.ModifierType), MapType(r.ElementType));
                case OptionalModifierType o: return new OptionalModifierType(MapType(o.ModifierType), MapType(o.ElementType));
                case SentinelType s: return new SentinelType(MapType(s.ElementType));
                case GenericInstanceType g:
                    var gi = new GenericInstanceType(MapType(g.ElementType));
                    foreach (var arg in g.GenericArguments) gi.GenericArguments.Add(MapType(arg));
                    return gi;
                case FunctionPointerType f:
                    var fp = new FunctionPointerType { ReturnType = MapType(f.ReturnType), HasThis = f.HasThis, ExplicitThis = f.ExplicitThis, CallingConvention = f.CallingConvention };
                    foreach (var p in f.Parameters) fp.Parameters.Add(new ParameterDefinition(MapType(p.ParameterType)));
                    return fp;
            }
            if (IsLocal(t))
            {
                if (_types.TryGetValue(t.FullName, out var d)) return d;
                throw new InvalidOperationException("type not found: " + t.FullName);
            }
            return _dst.ImportReference(t);
        }

        private MethodReference MapMethod(MethodReference m)
        {
            if (m is GenericInstanceMethod gim)
            {
                var g = new GenericInstanceMethod(MapMethod(gim.ElementMethod));
                foreach (var a in gim.GenericArguments) g.GenericArguments.Add(MapType(a));
                return g;
            }
            var decl = MapType(m.DeclaringType);
            if (decl is TypeDefinition td && IsLocalOrTarget(m.DeclaringType))
            {
                var key = MethodKey(m);
                var found = td.Methods.FirstOrDefault(x => MethodKey(x) == key);
                if (found != null) return found;
                throw new InvalidOperationException("method not found: " + m.FullName);
            }
            var r = new MethodReference(m.Name, _dst.TypeSystem.Void, decl)
            {
                HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention,
            };
            foreach (var gp in m.GenericParameters) r.GenericParameters.Add(new GenericParameter(gp.Name, r));
            r.ReturnType = MapType(m.ReturnType);
            foreach (var p in m.Parameters) r.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType)));
            return r;
        }

        private bool IsLocalOrTarget(TypeReference t) => !(t is TypeSpecification) && IsLocal(t);

        private FieldReference MapField(FieldReference f)
        {
            var decl = MapType(f.DeclaringType);
            if (decl is TypeDefinition td && IsLocalOrTarget(f.DeclaringType))
            {
                var found = td.Fields.FirstOrDefault(x => x.Name == f.Name);
                if (found != null) return found;
                throw new InvalidOperationException("field not found: " + f.FullName);
            }
            return new FieldReference(f.Name, MapType(f.FieldType), decl);
        }

        // Signatures only store a generic parameter's position (!0, !!1), so placeholder owners give the right encoding.
        private TypeReference _typeOwner;
        private MethodReference _methodOwner;

        private GenericParameter Positional(int position, GenericParameterType kind)
        {
            if (_typeOwner == null)
            {
                _typeOwner = new TypeReference("", "<NomadUnityCompatT>", _dst, null);
                _methodOwner = new MethodReference("<NomadUnityCompatM>", _dst.TypeSystem.Void, _typeOwner);
            }
            var list = kind == GenericParameterType.Type ? _typeOwner.GenericParameters : _methodOwner.GenericParameters;
            while (list.Count <= position)
                list.Add(kind == GenericParameterType.Type ? new GenericParameter("T" + list.Count, _typeOwner) : new GenericParameter("M" + list.Count, _methodOwner));
            return list[position];
        }

        // ---------- keys ----------

        public static string MethodKey(MethodReference m) =>
            m.Name + (m.HasGenericParameters ? "`" + m.GenericParameters.Count : "") + "("
            + string.Join(",", m.Parameters.Select(p => TypeKey(p.ParameterType)).ToArray()) + ")" + TypeKey(m.ReturnType)
            + (m.HasThis ? "" : "s");

        public static string TypeKey(TypeReference t)
        {
            switch (t)
            {
                case GenericParameter gp: return (gp.Type == GenericParameterType.Type ? "!" : "!!") + gp.Position;
                case ArrayType a: return TypeKey(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]";
                case ByReferenceType b: return TypeKey(b.ElementType) + "&";
                case PointerType p: return TypeKey(p.ElementType) + "*";
                case PinnedType p: return TypeKey(p.ElementType);
                case RequiredModifierType r: return TypeKey(r.ElementType);
                case OptionalModifierType o: return TypeKey(o.ElementType);
                case SentinelType s: return TypeKey(s.ElementType);
                case GenericInstanceType g:
                    return TypeKey(g.ElementType) + "<" + string.Join(",", g.GenericArguments.Select(TypeKey).ToArray()) + ">";
                case FunctionPointerType _: return "fnptr";
                default: return t.FullName;
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types)
            {
                yield return t;
                foreach (var n in AllTypes(t.NestedTypes)) yield return n;
            }
        }
    }
}
