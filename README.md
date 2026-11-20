# EnbNext

A new game client for **Earth & Beyond**, made for the [Net-7](https://www.net-7.org)
emulator. It plays the game as the original client does, on current systems, and adds an
optional modern interface and multibox tools.

EnbNext ships no game data. It reads everything from your own Earth & Beyond and Net-7
installs.

## What you need

- **Earth & Beyond** installed, and the **Net-7 launcher** installed (the normal Net-7 setup).
- A Net-7 account.
- **Windows 10 or 11, 64-bit.** A Linux build is published too, but it is experimental.

You do not need to install .NET or anything else: everything EnbNext needs comes in the download.

## Install and play

1. Download the latest `EnbNext-…-win-x64.zip` from [Releases](../../releases).
2. Unzip it to a folder of your own (for example `C:\Games\EnbNext`). Not inside the Earth & Beyond folder.
3. Start `EnbNext.exe`.
4. The first time, EnbNext looks for your Earth & Beyond and Net-7 installs. If it cannot
   find them, it asks you for the folders. It remembers your choice.
5. Log in with your Net-7 account and play.

EnbNext is not signed, so Windows may say it "protected your PC" the first time.
Choose **More info**, then **Run anyway**.

To update, download the new release and unzip it over the old folder (or into a new one).
Your settings are kept: they live in your user profile, not in the EnbNext folder.

### Linux (experimental)

Download `EnbNext-…-linux-x64.tar.gz`, unpack it, and run `./EnbNext` from that folder. You
still need the Earth & Beyond and Net-7 installs (for example in a Wine prefix); point
EnbNext at them when it asks.

Install two small packages from your distribution first: `ffmpeg` (the game's videos) and
`libsecret-tools` (for **Remember login**; your desktop's keyring stores the password). On
Debian or Ubuntu:

```
sudo apt install ffmpeg libsecret-tools
```

## What is different from the original client

- Change resolution, window size and display scaling in the game, without restarting.
- **Modern UI** (Options > Quality of Life): optional modern player, target and group
  frames, action bar, chat tabs and an editable layout with profiles. The classic interface
  stays available.
- **Multiboxing** (opt-in): launch a group of characters at once, follow the leader's
  target, fire, dock and wormholes, focus mode with thumbnails of your other clients,
  shared harvesting and your alts' reactors in the group frame. Every option is off until
  you turn it on.
- Settings per section for each character or for all characters at once.
- Optional texture packs (see below).

## Texture packs

Put a pack in its own folder under `TextureOverrides` in your EnbNext settings folder
(`%LOCALAPPDATA%\EnbNext\TextureOverrides\<pack name>` on Windows). A pack holds matching
`name.tga` and `name.dds` files using the original texture names (DDS: DXT1, DXT3 or DXT5,
same size as the TGA, with a full mip chain). Choose the pack in **Options > Graphics >
Textures**. Your game files are never changed.

## Problems and suggestions

Please open an issue on this repository. Say what you did, what happened, and what you
expected; a screenshot helps.

## Licence

EnbNext is MIT-licensed; see `LICENSE`. Third-party components are listed in
`THIRD_PARTY_NOTICES.md`.

Earth & Beyond is a trademark of Electronic Arts Inc. EnbNext is an independent project,
not affiliated with or endorsed by Electronic Arts or the Net-7 team.
