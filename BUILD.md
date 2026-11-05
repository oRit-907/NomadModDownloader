# Building Nomad Mod Downloader

The full source is in [`src/`](src). It's a .NET 8 WinForms app (C#), published as one self-contained Windows exe.

## What you need

- Windows 10 or 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

The NuGet packages restore by themselves on the first build: SharpCompress, Microsoft.Web.WebView2, System.Security.Cryptography.ProtectedData and AssetRipper.TextureDecoder.

## Build

From the `src` folder:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

The app is written to `src/publish/NomadModDownloader.exe`. It's deliberately not compressed (about 160 MB): compressed single-file exes look "packed" to antivirus scanners and get flagged. The version number comes from `<Version>` in `src/NomadModDownloader.csproj`.

## What the app does on your PC

- It reads and writes only the Nomad Drive game folder you choose, and its own folder `%AppData%\NomadModDownloader`: settings, the list of installed files, backups and downloads.
- Mods are downloaded only after you click Nexus's own download button, inside the app's built-in browser (WebView2).
- BepInEx is downloaded from its official GitHub releases, and only after you say yes.
- App updates come from this repo's GitHub releases. The new exe is swapped in by a small script, `apply-update.cmd`, after the app closes.
- Mod summaries can be translated with the MyMemory service. Only the summary text is sent.

## Third-party files

- Noto Sans font: SIL Open Font License, see `src/Art/OFL.txt`.
- The Discord icon is used only to link to the Nomad Drive Discord server.
- Nomad Drive's menu art is not included. The app reads it from each player's own game install.
