using System.Collections.Generic;
using BepInEx;
using Mono.Cecil;
using UnityEngine;
#if BEPINEX6
using BepInEx.Unity.Mono.Bootstrap;
#else
using BepInEx.Bootstrap;
#endif

namespace NomadModManager
{
    /// <summary>The few places BepInEx 5 and 6 differ, so the rest of the menu is the same code for both.</summary>
    internal static class Bep
    {
#if BEPINEX6
        public const int Major = 6;
        public static Dictionary<string, PluginInfo> Plugins => UnityChainloader.Instance.Plugins;
        public static GameObject Manager => UnityChainloader.ManagerObject;
        public static PluginInfo ToPluginInfo(TypeDefinition type, string dll) => UnityChainloader.ToPluginInfo(type, dll);
        public static bool VersionOk(BepInDependency d, PluginInfo have) => d.VersionRange == null || d.VersionRange.IsSatisfied(have.Metadata.Version);
#else
        public const int Major = 5;
        public static Dictionary<string, PluginInfo> Plugins => Chainloader.PluginInfos;
        public static GameObject Manager => Chainloader.ManagerObject;
        public static PluginInfo ToPluginInfo(TypeDefinition type, string dll) => Chainloader.ToPluginInfo(type);
        public static bool VersionOk(BepInDependency d, PluginInfo have) => d.MinimumVersion == null || have.Metadata.Version >= d.MinimumVersion;
#endif
    }
}
