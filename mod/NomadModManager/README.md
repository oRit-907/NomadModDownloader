# Nomad Mod Manager (in-game Mods menu)

A BepInEx 5 plugin that adds a **Mods** button to Nomad Drive's main menu, styled like Nomad Mod Downloader.

**Requires Nomad Mod Downloader 2.4.0 or newer**: install the app and open it once with your game folder set.

- **Browse mods**: Nomad Drive mods on Nexus, sorted into categories (Nexus files them all as Miscellaneous), with thumbnails.
- **Installed**: your mods grouped by author, with enable, disable, remove and update.
- **Install / Update** hand the mod to Nomad Mod Downloader (2.4.0 or newer), which opens its Nexus file ready to download.
- **No restart when it's safe**: new mods start straight away at the main menu. Changes to mods that are already running are queued and applied by a small preloader patcher the next time the game starts.

## Install

Unzip `NomadModManager-<version>.zip` into the Nomad Drive folder (it contains a `BepInEx` folder). Needs BepInEx 5.

| File | Goes to |
|---|---|
| `NomadModManager.dll` | `BepInEx\plugins\NomadModManager\` |
| `NomadModManager.Patcher.dll` | `BepInEx\patchers\` (applies queued changes at launch) |
| `libwebp.dll`, `libsharpyuv.dll` | `BepInEx\NomadModManager\native\` (decodes Nexus thumbnails, which are WebP) |

## Mod authors

The menu checks a new DLL before starting it without a restart. To decide for your mod yourself, add one line:

```csharp
[assembly: System.Reflection.AssemblyMetadata("NomadHotLoad", "true")]   // or "false" to always ask for a restart
```

## Building

Needs the .NET 8 SDK. The game's own DLLs aren't in this repo, so copy them from your install first:

1. `lib/`: everything in `Nomad Drive_Data\Managed\` and `BepInEx\core\`.
2. `lib/sys/`: `mscorlib.dll`, `System.dll` and `System.Core.dll` from `Nomad Drive_Data\Managed\`, plus `netstandard.dll` from the .NET Framework 4.7.2 reference assemblies' `Facades` folder.

The plugin compiles against the game's own code-stripped system libraries on purpose, so anything Unity stripped fails at build time instead of in the game.

```
dotnet build Patcher -c Release -o out/patchers
dotnet build Plugin -c Release -o out/plugins
```

libwebp and libsharpyuv are Google's official builds (BSD 3-Clause), from the `Imazen.WebP.NativeRuntime.win-x64` 1.6.1 package.
