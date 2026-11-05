# Nomad Mod Downloader

A Windows app that browses the **Nomad Drive** section of Nexus Mods and installs mods straight into `BepInEx\plugins`. It installs BepInEx for you if it's missing, but only after asking.

Download `NomadModDownloader.exe` from **[Releases](../../releases)**. It's a single file and needs no install or .NET.

## Features

- Browse, search and sort Nomad Drive mods, with a picker for any version, older ones included.
- Log in to Nexus once. **Install** opens the file's page with the download button highlighted. Click **Manual download → Slow download** and the app installs the mod automatically.
- Mods are placed automatically: loose DLLs go to `BepInEx\plugins\<Mod>\`, and archives with their own `BepInEx\…` layout are unpacked as laid out. Zip, 7z and rar all work.
- The **Installed** tab checks for updates, enables or disables mods, and uninstalls them cleanly, restoring any files a mod replaced.
- 12 languages, with live translation of Nexus mod summaries.
- Styled like Nomad Drive's own menus.

## Updates

The app checks this repo's latest release when it starts. If there's a newer version, it asks before downloading, swaps itself and restarts.

This repo is private, so GitHub needs a token to see the releases. The first time it checks, the app asks for one:

1. Go to GitHub → **Settings → Developer settings → Fine-grained tokens → Generate new token**.
2. Repository access: **Only select repositories → NomadModDownloader**.
3. Permissions: **Contents: Read-only**.
4. Paste the token into the app. It's stored encrypted for your Windows user.

## Publishing a new version

1. Bump `<Version>` in `NomadModDownloader.csproj`.
2. Build with `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish`.
3. Create a release tagged `vX.Y.Z`, matching the version, and attach `NomadModDownloader.exe`. The release notes are shown in the app's update prompt.

> The exe includes Nomad Drive's logo and menu backgrounds for its look. That art belongs to the game's developers, so keep this repo and its releases private.
