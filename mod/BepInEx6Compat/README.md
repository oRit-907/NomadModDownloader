# BepInEx 6 Compatibility (for BepInEx 5)

By AkoRit. A BepInEx 5 plugin that runs **BepInEx 6 (Unity Mono) mods on BepInEx 5**: the mirror image of
BepInEx 5 Compatibility. It loads the real BepInEx 6 runtime beside BepInEx 5 and starts BepInEx 6's own chainloader
on `BepInEx\plugins`.

- Each plugin is loaded once: BepInEx 6's chainloader only picks up plugins built for BepInEx 6, and BepInEx 5's only
  those built for 5.
- Harmony, MonoMod and Cecil are shared with BepInEx 5.
- BepInEx 6's log goes into BepInEx 5's `LogOutput.log` under the same source names. Its settings are in
  `BepInEx\config\BepInEx6.cfg` and its cache in `BepInEx\cache\BepInEx6`.
- Limits: BepInEx 6 *patchers* don't run, and a BepInEx 6 mod can't see BepInEx 5 mods as dependencies (or the other
  way round).

## Files

| File | Goes to |
|---|---|
| `BepInEx6Compat.dll` | `BepInEx\plugins\BepInEx6Compat\` |
| BepInEx 6.0.0-be.697's `BepInEx.Core.dll`, `BepInEx.Unity.Mono.dll`, `BepInEx.Unity.Common.dll`, `BepInEx.Preloader.Core.dll` (LGPL-2.1), `SemanticVersioning.dll`, `AssetRipper.Primitives.dll` (MIT) | `BepInEx\compat\BepInEx6\` |

Nomad Mod Downloader installs all of this with BepInEx 5 (`src/Assets/BepInEx6Compat.zip`).

## Building

Uses `../BepInEx5Compat/lib` (see that README). `dotnet build -c Release -o out`.
