# Unity Compatibility (BepInEx 6 patcher)

Nomad Drive is built with Unity's code stripping: its `UnityEngine.*.dll` files only contain what the game itself
uses. Mods that call anything else (for example `AssetBundle.LoadFromFile`, XR input, many Physics and Terrain
overloads) fail with `MissingMethodException: Method not found`.

This patcher restores that code before Unity loads it:

1. At preload (`CompatCache.Sync`) it merges each game module with the full module of the same Unity version from
   `BepInEx\unity-libs\<version>` (BepInEx's own Unity library download, `https://unity.bepinex.dev/libraries/<version>.zip`)
   and writes the result to `BepInEx\unity-compat`. `UnityMerger` only **adds** types, fields, methods, properties,
   events and type forwarders the game's module lacks; the game's own code stays exactly as built. Modules the game
   doesn't ship are copied as they are.
2. The result is cached (`manifest.txt`) and rebuilt only when the game's files, the libraries or the patcher change.
   If the libraries for the game's Unity version are missing, the folder is emptied so no mismatched files load.
3. `BepInEx\unity-compat` is on doorstop's `dll_search_path_override` (after `DotNetFix`), so BepInEx and Mono use the
   restored modules. The patcher also loads them early in its `Finalizer`, and merges in memory if the search path
   isn't set.

The Nomad Mod Downloader sets all of this up (Check BepInEx > Unity Compatibility).

## Build

```
dotnet build -c Release -o out
```

It references the BepInEx 6 DLLs in `../BepInEx5Compat/lib/bep6`. The downloader embeds the result as
`src/Assets/UnityCompat.zip` (`version.txt` + `BepInEx/patchers/NomadUnityCompat/NomadUnityCompat.dll`).
