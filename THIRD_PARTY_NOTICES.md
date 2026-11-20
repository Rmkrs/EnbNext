# Third-party notices

EnbNext itself is MIT-licensed (see `LICENSE`). It uses the
third-party components below. Each remains under its own licence; the full
licence texts ship with the respective packages and, for CEF, with every
build (`LICENSE.txt` and `CREDITS.html` next to the client).

| Component | Version | Licence | How it is used |
|---|---|---|---|
| SDL3 (via ppy.SDL3-CS) | ppy.SDL3-CS 2026.1002.1 | SDL3: zlib; ppy.SDL3-CS: MIT | Window, input, GL context |
| ANGLE (from the Electron release) | Electron 41.0.3 | ANGLE: BSD-3-Clause; Electron: MIT | OpenGL ES on Direct3D 11 (Windows); downloaded at build time |
| Chromium Embedded Framework (CEF) | 150.0.23+g4ccc4ca+chromium-150.0.7871.287 | BSD-3-Clause; bundled Chromium components under the licences listed in `CREDITS.html` | In-game web terminals; downloaded at build time |
| SkiaSharp | 3.119.4 | MIT (Skia: BSD-3-Clause) | Host UI surfaces and screenshots |
| HarfBuzzSharp | 8.3.1.3 | MIT (HarfBuzz: MIT-style "Old MIT") | Text shaping for SkiaSharp |
| NLayer | 3.0.1 | MIT | MP3 decoding (managed, all platforms) |
| Silk.NET.OpenAL | 2.23.0 | MIT | OpenAL bindings |
| OpenAL Soft (via Silk.NET.OpenAL.Soft.Native) | 1.23.1 | LGPL-2.0-or-later | Audio output; loaded as a separate, replaceable native library |
| .NET runtime and libraries | 10.0 | MIT | Runtime |

Test-only dependencies (not distributed with the client): xUnit 2.9.2 and
xunit.runner.visualstudio 2.8.2 (Apache-2.0), Microsoft.NET.Test.Sdk 17.11.1 (MIT).

## Optional system integration

On Linux, remembering account passwords and website login cookies uses the
system-installed libsecret `secret-tool` executable and an unlocked Secret
Service keyring. libsecret is LGPL-2.1-or-later; it is not bundled or linked into
EnbNext. See https://gitlab.gnome.org/GNOME/libsecret. Secrets are passed through
redirected standard input, never command arguments. Manual login does not require
this integration. Windows uses the operating system Credential Manager APIs.

## Not included

EnbNext contains no Earth & Beyond game data and no Net-7 data. The one
exception is the program icon (`EnbNext.ico` and `EnbNext.png`), which is the
original Earth & Beyond icon (`EB.ICO`), Electronic Arts' artwork; it is not
covered by EnbNext's MIT licence. At runtime EnbNext reads the game data from the
user's own installations:

- the Earth & Beyond retail install (assets, and RAD Game Tools' Bink video
  library `binkw32.dll`, which is loaded from that install and never
  redistributed);
- the Net-7 launcher install (`cdata.dat`, `GalaxyMap.dat`).

Earth & Beyond is a trademark of Electronic Arts Inc. EnbNext is an
independent project, not affiliated with or endorsed by Electronic Arts or the
Net-7 team.
