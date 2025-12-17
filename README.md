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

## Build & Run
### Rider
Open `Retromind.sln` and run the default configuration.

### CLI
    dotnet restore dotnet run --project Retromind.csproj

# Start directly in BigMode:

    dotnet run --project Retromind.csproj -- --bigmode

# or (if you run the built app directly)
    ./Retromind --bigmode

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

## Wayland note (video embedding)
On some Wayland setups, embedded video playback can be problematic. Retromind may force X11/XWayland
depending on configuration/platform behavior.

## License
GPL-3.0 (see `COPYING`).