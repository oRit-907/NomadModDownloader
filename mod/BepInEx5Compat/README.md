# BepInEx 5 Compatibility (for BepInEx 6)

A BepInEx 6 (Unity Mono) plugin that runs **BepInEx 5 mods on BepInEx 6**. BepInEx 6 can't load BepInEx 5 plugins
by itself yet, so this plugin loads the real BepInEx 5 runtime beside it and starts BepInEx 5's own chainloader on
`BepInEx\plugins`.

- Each plugin is loaded exactly once: BepInEx 5's chainloader only picks up plugins built for BepInEx 5, and BepInEx 6's
  only those built for 6.
- Harmony, MonoMod and Cecil are shared with BepInEx 6 (BepInEx 5 mods asking for older versions get BepInEx 6's newer ones).
- BepInEx 5's log goes into BepInEx 6's `LogOutput.log` under the same source names. Its settings are in
  `BepInEx\config\BepInEx5.cfg` and its cache in `BepInEx\cache\BepInEx5`.
- Limits: BepInEx 5 *patchers* that rewrite game DLLs don't run, and a BepInEx 5 mod can't see BepInEx 6 mods in
  `Chainloader.PluginInfos` (or the other way round).

## Files

| File | Goes to |
|---|---|
| `BepInEx5Compat.dll` | `BepInEx\plugins\BepInEx5Compat\` |
| BepInEx 5.4.23.5's `BepInEx.dll`, `BepInEx.Harmony.dll`, `HarmonyXInterop.dll`, `0Harmony20.dll` (MIT) | `BepInEx\compat\BepInEx5\` |

Nomad Mod Downloader installs all of this with BepInEx 6 (`src/Assets/BepInEx5Compat.zip`).

## Building

Put the DLLs in `lib/`: `lib/bep5` (BepInEx 5.4.23.5 `core`), `lib/bep6` (BepInEx 6.0.0-pre.2 Unity.Mono `core`) and
`lib/game` (`UnityEngine.dll`, `UnityEngine.CoreModule.dll` from `Nomad Drive_Data\Managed`). Then `dotnet build -c Release -o out`.
