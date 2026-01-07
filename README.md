# Retromind

Built by human creativity, powered by artificial intelligence.
Dedicated to the digital spark that helped compile this reality.

 
Retromind is a Linux-first, portable media manager for organizing and launching your media library
(games, movies, books, comics, ...).

Built with **C#** + **Avalonia**.

## Status
IMPORTANT:

Retromind is currently at version 0.1-alpha. Data formats (retromind_tree.json, app_settings.json) can change between releases without a migration path. Therefore, use this version more for testing than for a large, long-term library.

Work in progress. Expect breaking changes while features and data formats evolve.

## Key features (high-level)
- Library tree (areas/categories) on the left
- Cover/grid view in the center
- Details view on the right
- Metadata scraping (depends on your API keys)
- Optional BigMode / controller-friendly UI

## Requirements
- Linux (primary target)
- .NET SDK 10.0
- Optional: VLC (LibVLCSharp) for video playback

## Build AppImage (portable release, includes VLC)
This project ships a build script that creates a portable **AppImage** containing:
- a self-contained .NET build (no system .NET required)
- bundled **LibVLC + plugins** (video playback required)

Note: When using the AppImage, you do not need a system-wide VLC installation because LibVLC is bundled.
The Wayland/X11 note below still applies because it affects how video is embedded into the Avalonia window.

### Requirements (host)
- Docker (for reproducible LibVLC export)
- `curl` (to download `appimagetool` if missing)

### Build
```
chmod +x build/AppRun build/build-appimage.sh 
./build/build-appimage.sh
```

The resulting AppImage will be created at:
- `dist/Retromind-x86_64.AppImage`

## Build & Run
### Rider

Open `Retromind.sln` and run the default configuration.

### CLI
```bash
dotnet restore
dotnet run --project Retromind.csproj
```

Start directly in BigMode:
```bash
dotnet run --project Retromind.csproj -- --bigmode
```

Or (if you run the built app directly):
```bash
./Retromind --bigmode
```

## Getting started (first run)
1. Build and run once (see “Build & Run”). Retromind will create `app_settings.json` in the app directory (portable).
2. If you want to preconfigure settings, copy `app_settings.sample.json` to `app_settings.json` and adjust values.
3. For metadata scraping, configure API keys (see “API keys / Secrets”).

## Configuration (portable)
Retromind stores data in/near the application directory for portability.
Make sure the folder is writable.

Ignored runtime files (not committed):
- `Library/`
- `app_settings.json`
- `retromind_tree.json` (+ `.bak` / `.tmp`)

A sample settings file is provided:
- `app_settings.sample.json`

### LibVLC hardware decoding (BigMode previews)

Retromind uses **LibVLC** for video previews in BigMode.  
The hardware decoding mode is configurable via `app_settings.json`:

```jsonc
"VlcHardwareDecodeMode": "none" // or "auto", "vaapi"
```

Supported values (depend on the host system / VLC build):

- `"none"`  
  Always use software decoding.  
  Safest default for unknown systems and portable AppImage builds.

- `"auto"`  
  Let VLC/FFmpeg pick a suitable hardware backend if available.  
  Good compromise on well-configured desktop systems.

- `"vaapi"`  
  Force VAAPI hardware decoding on compatible Linux systems (Intel/AMD iGPU).  
  Can noticeably reduce CPU usage and make high-resolution videos smoother,
  but may fail on systems with broken/incomplete VAAPI setups.

If the value is missing or invalid, Retromind falls back to `"none"`.

For the **AppImage**, `"none"` is recommended as default for maximum compatibility.  
On your own machine you can set `"vaapi"` in `app_settings.json` if VAAPI works
well (e.g. smoother BigMode videos, lower CPU load).

### Portable layout on USB sticks / external drives

Retromind is designed to work well from a single portable folder (e.g. on a USB stick)
together with your ROMs and native games. The core idea:

- The directory that contains the Retromind binary/AppImage is treated as the **portable data root**.
- Any files *inside* this directory (or subdirectories) are stored as **relative paths** in the library.
- On another Linux system, as long as you copy/mount the entire directory tree, Retromind will
  resolve these relative paths correctly, regardless of the exact mountpoint or user name.

A practical layout might look like this:
```text
Retromind/ Retromind-x86_64.AppImage Library/ ROMs/ SNES/ PSX/ NativeGames/ MyPortedGame/ WinePrefixes/ 123e4567-..._Some_Wine_Game/ Themes/
```

If you add ROMs or native games from anywhere *inside* the `Retromind/` folder:

- Retromind will detect that their absolute paths are under the portable root,
- convert them once to **library-relative** paths in the JSON database,
- and resolve them at runtime against the current AppImage directory.

This means:

- Moving the entire `Retromind/` folder to another machine or mounting it under a different path
  will **not** break those entries.
- Only data stored outside of `Retromind/` (e.g. `/home/user/Downloads/…`) is saved as an absolute path
  and depends on the original mountpoint.

### Wine prefixes and portability

When launching items that use Wine/Proton/UMU, Retromind can automatically create and
remember a **per-item Wine prefix** in the library:

- Prefixes are stored under `Library/Prefixes/…` (inside the portable root).
- The stored prefix path is **relative** to the library root.
- On another system, as long as the whole `Retromind/` folder moves together,
  the same prefixes will be reused.

Note:

- The prefix itself is portable within Retromind’s folder.
- Game saves and configs that a title writes into `~/.config` or `~/.local/share` remain
  user-specific and are not automatically moved with the USB stick.

### Native games on the stick

Native games that live under the `Retromind/` directory tree (e.g. `Retromind/NativeGames/MyGame/...`)
are resolved the same way as ROMs:

- Internally, Retromind stores their launch paths relative to the portable root.
- On a different Linux system, launching still works as long as:
  - the game files remain in the same relative position under `Retromind/`,
  - system-level dependencies (e.g. libraries, drivers) required by the game are available.

Game-specific saves/configs stored under the user’s home directory are not moved automatically;
they will behave like any regular native Linux game when run on a different machine.

## Launch arguments placeholders

When configuring emulator profiles or per-item launch arguments, Retromind supports a few simple placeholders that are expanded at launch time:

- `{file}`  
  Full path to the primary launch file (quoted when needed).
- `{fileDir}`  
  Directory of the primary launch file (no trailing slash).
- `{fileName}`  
  File name including extension (e.g. `cabal.zip`).
- `{fileBase}`  
  File name without extension (e.g. `cabal`).

These placeholders can be used in both:

- **Emulator profile arguments** (`EmulatorConfig.Arguments`)
- **Per-item arguments** (`MediaItem.LauncherArgs`), which are combined with the profile arguments.

### Example: Flatpak MAME via emulator profile

To launch the Flatpak MAME build using ROM short names derived from the file path:

- **Executable path**:  
  `flatpak`
- **Default arguments**:  
  `run org.mamedev.MAME {fileBase}`

With a ROM stored as:
```text
/run/media/…/MAME/NameOfROM.zip
```

Retromind expands `{fileBase}` to `NameOfROM` and starts:
```bash
flatpak run org.mamedev.MAME NameOfROM
```

Make sure the ROM directory is part of MAME’s `rompath`, or pass it explicitly:
```text
run org.mamedev.MAME -rompath "{fileDir}" {fileBase}
```

which yields, for the example above:
```bash
flatpak run org.mamedev.MAME -rompath "/run/media/…/MAME" NameOfROM 
```

### Example: Wine / UMU wrappers

For Wine-based games (e.g. via UMU) you can keep most logic in the emulator profile:
```text
umu-run --some-default-options {file} 
```

and use per-item arguments only for game-specific flags, e.g.:
```text
--use-special-mode 
```

Retromind combines profile + item arguments into a single command line while expanding the placeholders as described above.

## API keys / Secrets (Scrapers)
Retromind does **not** use any bundled default keys at runtime.  
All scraper providers (TMDB, IGDB, Google Books, …) read their API credentials
from the scraper configuration (e.g. via the settings dialog). If no key is
configured, the corresponding scraper simply cannot be used.

Secrets are not stored in plain text. The app persists only encrypted fields
(e.g. `EncryptedApiKey`).

A template is provided for local development experiments:
- `Helpers/ApiSecretsTemplate.cs`

> NOTE: The main Retromind application does **not** use `ApiSecrets` for
> scraping. This template is only for custom tools or debugging scenarios.

### Where to get API keys

You need to create your own API keys on the respective provider pages:

- **TMDB (The Movie Database)**  
  Create a free account at:  
  https://www.themoviedb.org/  
  Then go to *Settings → API* in your profile and request an API key (v3 auth).
  Enter this key in the TMDB scraper configuration in Retromind.

- **IGDB (via Twitch Developer)**
  1. Create a Twitch Developer account:  
     https://dev.twitch.tv/
  2. In the Developer Console, create an application to obtain:
    - `Client ID`
    - `Client Secret`
  3. Enter both values in the IGDB scraper configuration in Retromind.

- **Google Books (optional)**  
  The Google Books API can be used without a key in many cases, but you may
  configure an API key to raise limits:  
  https://console.cloud.google.com/apis/library/books.googleapis.com  
  Create a project, enable the Books API, and create an API key. Enter it in
  the Google Books scraper configuration in Retromind.

Each user is responsible for their own API keys and must comply with the
respective provider terms of service.

## Wayland / X11 note (VLC video embedding)
Embedded video playback via **LibVLCSharp** can be unreliable on some Wayland setups (depending on compositor and XWayland behavior).
To make VLC embedding work consistently, Retromind defaults to **X11 via XWayland** on Linux by setting `AVALONIA_PLATFORM=x11` early at startup.

You can override this behavior for testing:

- Force X11 (default):
  ```bash
  dotnet run --project Retromind.csproj -- --avalonia-platform=x11
  ```

- Force Wayland (experimental / compositor-dependent):
  ```bash
  dotnet run --project Retromind.csproj -- --avalonia-platform=wayland
  ```

- Let Avalonia decide automatically:
  ```bash
  dotnet run --project Retromind.csproj -- --avalonia-platform=auto
  ```

## Architecture
See [`Docs/architecture.md`](Docs/architecture.md).

## Contributing

Contributions are welcome!  
Before opening issues or pull requests, please have a look at:

- [`CONTRIBUTING.md`](CONTRIBUTING.md) – contribution guidelines
- [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) – expected behavior in the project community

## License
GPL-3.0-only (see `COPYING`).