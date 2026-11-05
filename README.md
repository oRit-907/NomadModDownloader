# Nomad Mod Downloader

A Windows app that browses the **Nomad Drive** section of Nexus Mods and installs mods straight into `BepInEx\plugins`. It installs BepInEx for you if it's missing, but only after asking.

Download `NomadModDownloader.exe` from **[Releases](../../releases)**. It's a single file and needs no install or .NET.

## Features

- Browse, search and sort Nomad Drive mods, with a picker for any version, older ones included.
- Log in to Nexus once. **Install** opens the file's page with the download button highlighted. Click **Manual download → Slow download** and the app installs the mod automatically.
- Mods are placed automatically: loose DLLs go to `BepInEx\plugins\<Mod>\`, and archives with their own `BepInEx\…` layout are unpacked as laid out. Zip, 7z and rar all work.
- The **Installed** tab lists mods grouped by author, checks for updates, enables or disables mods, and uninstalls them cleanly, restoring any files a mod replaced.
- **Play** and **Play without mods** buttons. Mods switch back on by themselves after a vanilla run.
- **Problems** reads the game's logs after each session and says which mod failed and why: missing requirements, duplicates, errors, crashes and conflicts. **Copy for a bug report** puts it all on the clipboard.
- **Mod requirements**: if a mod needs other mods, the app offers to install those first.
- **Update all**, with a count on the menu at startup and each mod's changelog before updating.
- **Mod settings**: edit any mod's `BepInEx\config` file with switches, lists and boxes instead of a text editor.
- **Profiles**: save sets of mods ("Vanilla+", "Cheats"…) and switch in one click. **Share codes** let friends install the same set.
- **Saves**: backs up your Nomad Drive saves before mod changes, with one-click restore.
- Warns you when Nomad Drive itself updates, with a quick "turn all mods off".
- Drag a mod's zip, 7z, rar or dll onto the window to install it.
- **Endorse** button that takes you to the mod's Endorse button on Nexus.
- A Discord button next to PLAY for the Nomad Drive community.
- Works with the in-game **Mods** menu (Nomad Mod Manager): its **Install** and **Update** buttons open the mod here, ready to download, and mods can be installed while the game is running. Files that are in use are swapped in the next time the game starts.
- 12 languages, with live translation of Nexus mod summaries.
- Styled like Nomad Drive's own menus. The menu backgrounds are read from your own copy of the game, and the logo from Steam's library cache, so no game art ships with the app.

## In-game Mods menu

The repo also holds **Nomad Mod Manager**, a BepInEx plugin that adds a Mods menu to Nomad Drive's main menu (source and build steps in [`mod/NomadModManager`](mod/NomadModManager)). Each release has its zip next to the app. The in-game menu needs this app (2.4.0 or newer): its Install buttons hand mods to it.

## Finding the game

The app finds Nomad Drive by itself: through Steam (any library, even a renamed install folder), the running game, Windows' installed-programs list, Epic and GOG, and finally a quick search of your drives. If it still can't, click **Change folder…**. Picking a folder inside the game, or the folder that contains it, also works.

## Updates

An update screen checks this repo's latest release each time the app starts. If there's a newer version, it downloads it with a progress bar, swaps itself and restarts. Offline or slow, it gives up after a few seconds and the app opens as normal. No GitHub account or token is needed. To be asked first instead, set `AutoUpdateApp: false` in `%AppData%\NomadModDownloader\settings.json`.

BepInEx is kept on the latest BepInEx 5 too, but only while the game is closed. Your plugins, configs and `doorstop_config.ini` are kept.

## Source code

The full source is in [`src/`](src). [BUILD.md](BUILD.md) explains how to build it and what the app does on your PC.

## Publishing a new version

Releases follow the same process as Ignite3D. They're built in a Claude cloud session and published by `.github/workflows/release.yml`:

1. Bump `<Version>` in `NomadModDownloader.csproj` and build with `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`.
2. From `main`, push a branch `release/vX.Y.Z` with a `release-files/` folder containing `NomadModDownloader-X.Y.Z.exe`, `NomadModDownloader-X.Y.Z-win-x64.zip`, an optional `notes.md`, and **`SHA256SUMS` last**. Split any file over 100 MB into `.part00`, `.part01`, and so on.
3. The workflow checks the hashes, creates release `vX.Y.Z` on the `main` commit (the tag doesn't carry the files), marks it Latest, and deletes the branch.

> Don't add Nomad Drive's art (logo, menu backgrounds) to the app or this repo. It belongs to the game's developers; the app reads it from each player's own install.
