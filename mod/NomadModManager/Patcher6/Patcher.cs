using System;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace NomadModManager
{
    /// <summary>
    /// BepInEx 6 preloader patcher: applies mod updates, removals and enable/disable changes that were queued while the
    /// game was running. Patchers are created before BepInEx reads any plugin, so the work happens in the constructor.
    /// </summary>
    [PatcherPluginInfo("akorit.nomadmodmanager.patcher", "Nomad Mod Manager", "1.1.1")]
    public sealed class NomadModManagerPatcher : BasePatcher
    {
        public NomadModManagerPatcher()
        {
            try
            {
                if (!Pending.HasWork(Paths.GameRootPath)) return;
                var n = Pending.ApplyAll(Paths.GameRootPath, m => Log.LogInfo(m));
                Log.LogMessage($"Applied {n} queued mod change(s).");
            }
            catch (Exception e)
            {
                Log.LogError("Couldn't apply queued mod changes: " + e);
            }
        }
    }
}
