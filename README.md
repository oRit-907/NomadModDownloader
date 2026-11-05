# Nomad Mod Downloader

A Windows app that browses the **Nomad Drive** section of Nexus Mods and installs mods straight into `BepInEx\plugins`. It installs BepInEx for you if it's missing, but only after asking.

Download `NomadModDownloader.exe` from **[Releases](../../releases)**. It's a single file and needs no install or .NET.

## Features

- Browse, search and sort Nomad Drive mods, with a picker for any version, older ones included.
- Log in to Nexus once. **Install** opens the file's page with the download button highlighted. Click **Manual download → Slow download** and the app installs the mod automatically.
- Mods are placed automatically: loose DLLs go to `BepInEx\plugins\<Mod>\`, and archives with their own `BepInEx\…` layout are unpacked as laid out. Zip, 7z and rar all work.
- The **Installed** tab checks for updates, enables or disables mods, and uninstalls them cleanly, restoring any files a mod replaced.
- 12 languages, with live translation of Nexus mod summaries.
- Styled like Nomad Drive's own menus. The menu backgrounds are read from your own copy of the game, and the logo from Steam's library cache, so no game art ships with the app.

## Finding the game

The app finds Nomad Drive by itself: through Steam (any library, even a renamed install folder), the running game, Windows' installed-programs list, Epic and GOG, and finally a quick search of your drives. If it still can't, click **Change folder…**. Picking a folder inside the game, or the folder that contains it, also works.

## Updates

The app checks this repo's latest release when it starts. If there's a newer version, it downloads it, swaps itself and restarts. No GitHub account or token is needed. To be asked first instead, set `AutoUpdateApp: false` in `%AppData%\NomadModDownloader\settings.json`.

BepInEx is kept on the latest BepInEx 5 too, but only while the game is closed. Your plugins, configs and `doorstop_config.ini` are kept.

## Publishing a new version

Releases follow the same process as Ignite3D. They're built in a Claude cloud session and published by `.github/workflows/release.yml`:

1. Bump `<Version>` in `NomadModDownloader.csproj` and build with `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish`.
2. From `main`, push a branch `release/vX.Y.Z` with a `release-files/` folder containing `NomadModDownloader-X.Y.Z.exe`, `NomadModDownloader-X.Y.Z-win-x64.zip`, an optional `notes.md`, and **`SHA256SUMS` last**. Split any file over 100 MB into `.part00`, `.part01`, and so on.
3. The workflow checks the hashes, creates release `vX.Y.Z` on the `main` commit (the tag doesn't carry the files), marks it Latest, and deletes the branch.

> Don't add Nomad Drive's art (logo, menu backgrounds) to the app or this repo. It belongs to the game's developers; the app reads it from each player's own install.
