using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace NomadModManager
{
    /// <summary>
    /// Runs in BepInEx's preloader, before any plugin loads: applies mod updates, removals and
    /// enable/disable changes that were queued while the game was running.
    /// </summary>
    public static class NomadModManagerPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            var log = Logger.CreateLogSource("Nomad Mod Manager");
            try
            {
                if (!Pending.HasWork(Paths.GameRootPath)) return;
                var n = Pending.ApplyAll(Paths.GameRootPath, m => log.LogInfo(m));
                log.LogMessage($"Applied {n} queued mod change(s).");
            }
            catch (Exception e)
            {
                log.LogError("Couldn't apply queued mod changes: " + e);
            }
            finally
            {
                Logger.Sources.Remove(log);
            }
        }
    }
}
