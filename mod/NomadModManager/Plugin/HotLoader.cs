using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using UnityEngine;

namespace NomadModManager
{
    internal sealed class HotVerdict
    {
        public string Dll;
        public bool CanLoad;
        public bool NotAPlugin;       // a support library: nothing to start, it loads when something needs it
        public bool WrongGame;        // BepInProcess filter excludes Nomad Drive
        public string Reason = "";    // why it needs a restart
        public readonly List<PluginInfo> Plugins = new List<PluginInfo>();
    }

    /// <summary>
    /// Loads a plugin DLL that appeared after BepInEx's startup, the same way the chainloader does,
    /// when it's safe to; otherwise says why a restart is needed.
    /// </summary>
    internal static class HotLoader
    {
        /// <summary>GUID prefixes of mods known to start cleanly when loaded late.</summary>
        private static readonly string[] Trusted = { "nomaddrive.", "akorit." };

        /// <summary>Methods that have already run by the time you're at the main menu; patching them late does nothing.</summary>
        private static readonly HashSet<string> StartupMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Awake", "Start", "OnEnable", "Configure", "Install", "InstallBindings", "Initialize", "Init", "Construct", "Bootstrap" };

        private static bool _resolverHooked;

        public static HotVerdict Check(string dll)
        {
            var v = new HotVerdict { Dll = dll };
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(dll, TypeLoader.ReaderParameters); }
            catch (Exception e) { v.Reason = "Couldn't read the DLL (" + e.GetType().Name + ")"; return v; }

            using (asm)
            {
                if (!asm.MainModule.AssemblyReferences.Any(r => r.Name == "BepInEx"))
                {
                    v.NotAPlugin = true;
                    v.Reason = "Support library";
                    return v;
                }
                if (asm.MainModule.AssemblyReferences.Any(r => r.Name == "BepInEx.Preloader") || HasPatcherEntry(asm))
                {
                    v.Reason = "Preloader patcher";
                    return v;
                }

                foreach (var t in asm.MainModule.Types)
                {
                    PluginInfo info = null;
                    try { info = Chainloader.ToPluginInfo(t); } catch { }
                    if (info == null) continue;
                    SetProp(info, "Location", dll);
                    v.Plugins.Add(info);
                }
                if (v.Plugins.Count == 0) { v.NotAPlugin = true; v.Reason = "Support library"; return v; }

                var name = asm.Name.Name;
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => SafeName(a) == name))
                {
                    v.Reason = "An older copy is already running";
                    return v;
                }

                var batch = new HashSet<string>(v.Plugins.Select(p => p.Metadata.GUID));
                foreach (var p in v.Plugins)
                {
                    var procs = p.Processes.ToList();
                    if (procs.Count > 0 && procs.All(x => !string.Equals(x.ProcessName.Replace(".exe", ""), Paths.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    {
                        v.WrongGame = true; v.Reason = "Made for a different game"; return v;
                    }
                    if (Chainloader.PluginInfos.ContainsKey(p.Metadata.GUID))
                    {
                        v.Reason = "Replaces a mod that's already running"; return v;
                    }
                    foreach (var d in p.Dependencies.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
                    {
                        if (batch.Contains(d.DependencyGUID)) continue;
                        if (!Chainloader.PluginInfos.TryGetValue(d.DependencyGUID, out var have) || have.Instance == null)
                        { v.Reason = "Needs " + d.DependencyGUID + ", which isn't loaded"; return v; }
                        if (d.MinimumVersion != null && have.Metadata.Version < d.MinimumVersion)
                        { v.Reason = "Needs a newer " + have.Metadata.Name; return v; }
                    }
                    foreach (var inc in p.Incompatibilities)
                        if (Chainloader.PluginInfos.ContainsKey(inc.IncompatibilityGUID))
                        { v.Reason = "Conflicts with " + inc.IncompatibilityGUID; return v; }
                }

                // The mod can say for itself: [assembly: AssemblyMetadata("NomadHotLoad", "true" | "false")]
                var opt = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                                                                   && a.ConstructorArguments.Count == 2 && (a.ConstructorArguments[0].Value as string) == "NomadHotLoad");
                var optValue = opt?.ConstructorArguments[1].Value as string;
                if (string.Equals(optValue, "false", StringComparison.OrdinalIgnoreCase)) { v.Reason = "The mod asks for a restart"; return v; }
                var trusted = string.Equals(optValue, "true", StringComparison.OrdinalIgnoreCase)
                              || v.Plugins.All(p => Trusted.Any(t => p.Metadata.GUID.StartsWith(t, StringComparison.OrdinalIgnoreCase)));

                if (!trusted)
                {
                    var startup = FindStartupPatch(asm);
                    if (startup != null) { v.Reason = "Changes game startup code (" + startup + ")"; return v; }
                }

                v.CanLoad = true;
                return v;
            }
        }

        /// <summary>Loads the DLL and starts its plugins on BepInEx's manager object, like the chainloader would have.</summary>
        public static List<string> Load(HotVerdict v)
        {
            HookResolver();
            var started = new List<string>();
            var asm = Assembly.LoadFrom(v.Dll);
            var host = Chainloader.ManagerObject;
            if (host == null)
            {
                host = new GameObject("NomadModManager_HotLoaded");
                UnityEngine.Object.DontDestroyOnLoad(host);
            }

            foreach (var info in Order(v.Plugins))
            {
                var type = asm.GetType(GetProp<string>(info, "TypeName"), true);
                Chainloader.PluginInfos[info.Metadata.GUID] = info;
                try
                {
                    var instance = (BaseUnityPlugin)host.AddComponent(type);
                    SetProp(info, "Instance", instance);
                    started.Add(info.Metadata.Name + " " + info.Metadata.Version);
                }
                catch
                {
                    Chainloader.PluginInfos.Remove(info.Metadata.GUID);
                    throw;
                }
            }
            return started;
        }

        // ------------------------------------------------------------------

        private static IEnumerable<PluginInfo> Order(List<PluginInfo> plugins)
        {
            var left = plugins.ToList();
            var done = new HashSet<string>();
            while (left.Count > 0)
            {
                var next = left.FirstOrDefault(p => p.Dependencies.All(d => done.Contains(d.DependencyGUID) || !left.Any(o => o.Metadata.GUID == d.DependencyGUID)))
                           ?? left[0];
                left.Remove(next);
                done.Add(next.Metadata.GUID);
                yield return next;
            }
        }

        private static bool HasPatcherEntry(AssemblyDefinition asm) =>
            asm.MainModule.Types.Any(t => t.Properties.Any(p => p.Name == "TargetDLLs" && p.GetMethod != null && p.GetMethod.IsStatic)
                                       && t.Methods.Any(m => m.Name == "Patch" && m.IsStatic));

        /// <summary>Finds a [HarmonyPatch(typeof(X), "Awake")]-style patch on a startup method.</summary>
        private static string FindStartupPatch(AssemblyDefinition asm)
        {
            foreach (var t in AllTypes(asm.MainModule.Types))
            {
                foreach (var attrs in new[] { t.CustomAttributes }.Concat(t.Methods.Select(m => m.CustomAttributes)))
                {
                    foreach (var a in attrs)
                    {
                        if (!a.AttributeType.Name.StartsWith("HarmonyPatch")) continue;
                        TypeReference target = null;
                        string method = null;
                        foreach (var arg in a.ConstructorArguments)
                        {
                            if (arg.Value is TypeReference tr) target = tr;
                            else if (arg.Value is string s) method = s;
                        }
                        if (method != null && StartupMethods.Contains(method))
                            return (target != null ? target.Name + "." : "") + method;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types)
            {
                yield return t;
                foreach (var n in AllTypes(t.NestedTypes)) yield return n;
            }
        }

        /// <summary>Lets a late-loaded mod find DLLs that sit next to it in BepInEx\plugins.</summary>
        private static void HookResolver()
        {
            if (_resolverHooked) return;
            _resolverHooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name;
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => SafeName(a) == name);
                    if (loaded != null) return loaded;
                    var file = Directory.GetFiles(Paths.PluginPath, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                    return file != null ? Assembly.LoadFrom(file) : null;
                }
                catch { return null; }
            };
        }

        private static string SafeName(Assembly a)
        {
            try { return a.GetName().Name; } catch { return ""; }
        }

        internal static T GetProp<T>(object target, string name)
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return (T)p?.GetGetMethod(true)?.Invoke(target, null);
        }

        internal static void SetProp(object target, string name, object value)
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p?.GetSetMethod(true)?.Invoke(target, new[] { value });
        }
    }
}
