# Retromind

Retromind is a Linux-first, portable media manager for organizing and launching your media library
(games, movies, books, comics, ...).

Built with **C#** + **Avalonia**.

## Status
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
```
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
This repository does **not** contain real API keys.

Secrets are not stored in plain text. The app persists only encrypted fields (EncryptedApiKey, etc.).

A template is provided:
- `Helpers/ApiSecretsTemplate.cs`

To configure locally:
1. Copy/rename it to `Helpers/ApiSecrets.cs`
2. Rename the class `ApiSecretsTemplate` to `ApiSecrets`
3. Insert your personal API keys
4. Ensure `ApiSecrets.cs` stays ignored by Git

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

## License
GPL-3.0-only (see `COPYING`).