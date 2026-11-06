# Third-party notices

## DotNetFix (src/Assets/DotNetFix.zip)

Nomad Drive ships a code-stripped `mscorlib.dll` that BepInEx's preloader can't start with. The app installs full
class libraries into the game's `DotNetFix` folder and points doorstop at them (`dll_search_path_override`).

- **Mono class libraries** (mscorlib, System, System.Core, System.Xml and the other `System.*` assemblies from the
  "Mono development team"). Copyright (c) Various Mono authors. Licensed under the MIT License:
  https://github.com/mono/mono/blob/main/LICENSE
- **netstandard.dll** (the .NET Standard 2.1 facade) and **Mono.Security.dll**, from the official Mono 6.12.0.206
  Windows build (https://download.mono-project.com). MIT License, as above. Nomad Drive ships no netstandard.dll, so
  mods built for .NET Standard 2.0/2.1 need it; Mono.Security matches the full System.dll above.
- **System.Collections.Immutable, System.Runtime.CompilerServices.Unsafe, System.Memory, System.Buffers**
  (where from Microsoft). Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License:
  https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

MIT License: Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including without
limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.

## Unity Compatibility (mod/UnityCompat)

The patcher is this project's own code (it uses Mono.Cecil, MIT, which ships with BepInEx). The app doesn't ship
Unity's libraries: when you set up Unity Compatibility it downloads the ones for your game's Unity version from
BepInEx's server (https://unity.bepinex.dev/libraries/), the same files BepInEx itself uses, into BepInEx\unity-libs.
Unity is a trademark of Unity Technologies; its libraries remain under Unity's own terms.

## Nomad Mod Manager (mod/NomadModManager)

libwebp and libsharpyuv: Copyright (c) Google Inc. BSD 3-Clause License:
https://chromium.googlesource.com/webm/libwebp/+/refs/heads/main/COPYING
