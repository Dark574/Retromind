# Changelog

All notable changes to **Retromind** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),  
and this project (aims to) adhere to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---
## [0.1.7-alpha] - unreleased

### Added
- experimental native Wayland opt-in with X11 initialization fallback
- mass switching of proton versions
- drag & drop for items is now possible
- option to test launch items (without game time played counter)
- statistics for Library
- new system theme for Playstation 1

### Changed
- updated Avalonia to 12.1.1 and Avalonia WebView to 12.1.0
- expand and unify item context menus

### Fixed
- wayland starts in fullscreen
- cleaned up code and fixed a few small bugs

---

## [0.1.6-alpha] - 2026-08-26

### Added
- new System theme 'PC Games'
- text completion for custom fields, custom fields are shown before the item description
- new console system theme
- language support for texts in default system theme
- zsync metadata and embedded GitHub Releases update information for AppImage delta updates

### Changed
- removed Prefix architecture / selection is not used in Wine/Proton and misleading
- hardened GOG installer/ safety procedures for install/deinstall in system folders
- better name comparison for mass scraping
- unified save/abort handling for menus
- node names are now case sensitive
- use appimagetool 1.9.1 and static Type-2 runtime for build process

### Fixed
- theme sync didnt work correctly for system themes
- better error handling when saving data
- only load backup if the main file is missing/not readable/damaged
- a few small bugs and changes
- GOG installations are now portable when installed under dataroot

---

## [0.1.5-alpha] - 2026-06-25

### Added
- new system themes for the Commodore 64 and Amiga (more will follow soon)
- SteamGridDB as a new artwork scraper
- Added support for empty media entries without a launch file

### Changed
- Reworked the metadata and artwork import workflow with per-field selection
- Improved multi-disc filename detection and handling
- Improved GE-Proton runner management, sorting, registration and removal
- Improved video transitions when switching system themes

### Fixed
- crash when cancelling bulk scraping or opening the metadata dialog
- several smaller issues and removed unused code

---

## [0.1.4-alpha] - 2026-06-06

### Added
- you can now save search terms for quick reuse
- metadata autocomplete

### Changed
- removed Heroic GOG import functionality, since GOG is now supported natively
- only the x86_64 release of GE-Proton is now offered

### Fixed
- several small bugs

---

## [0.1.3-alpha] - 2026-06-01

### Added
- GOG integration (experimental):
    Update, deinstall and cancel installation of GOG titles directly in Retromind.
    Previously only Heroic‑based imports were supported.
    This implementation is currently experimental, so expect bugs.
    See the README for setup requirements and usage instructions.

### Changed
- small improvements to the search functionality

---

## [0.1.2-alpha] - 2026-05-18

### Added
- native GOG support, previous versions only supported GOG through Heroic import.
This implementation is currently experimental, so expect bugs.
See the README for setup requirements and usage instructions
- improved and more logical library sorting
- pressing Esc can now deselect the current item

### Fixed
- several smaller bugs and internal cleanup

---

## [0.1.1-alpha] - 2026-05-03

### Added
- management for wine and proton versions, download and refresh of GE-Proton releases

### Changed
- dynamic custom field handling and year comparison for search function

### Fixed
- a few small bug fixes and changes

---

## [0.1.0-alpha] - 2026-03-21

### Added
- Scraper support for the new metadata (if available)
- Scrapers are now configurable in the settings

### Changed
- separate metadata tab in edit medium dialog for cleaner menus

### Fixed
- included SDL2 library in AppImage, reworked build process to only use Debian 12 bookworm libraries
- a few small bugs

---

## [0.0.9-alpha] - 2026-04-10

### Added
- emulator-level XDG/HOME override logic in Emulator Settings (standard is that all emulators use system home/Xdg)
- Screenshot as a new media asset type for media items
- parental controls
- an explicit Tmds.DBus.Protocol dependency to address the transitive security issue in Avalonia.FreeDesktop:
GHSA-xrw6-gwf8-vvr9
- metadata fields: Release Type, Max Players, Publisher, Series, Play Mode, Source, Sort Title, Custom Fields

### Fixed
- AppImage environment sanitization (DocumentService and Winetricks are now included).

---

## [0.0.8-alpha] - 2026-03-06

### Added
- new scraper TheGamesDB
- scraper language selection where available
- support for XDG environment variable overrides in Emulators

### Changed
- manual metadata search dialog reworked (more results and richer info)

### Fixed
- included SDL2 library in AppImage, reworked build process to only use Debian 12 bookworm libraries
- a few small bugs

---

## [0.0.7-alpha] - 2026-03-12

### Added
- Heroic/Epic integration.
- Node settings can now apply a default emulator to existing items, including subcategories.
- Support for .cbz files in manuals.
- New HorizontalRow theme.
- Deferred startup warmup for faster initial loading.

### Changed
- Random music is now selected on each media-item selection (when randomization is active and multiple music files are available).
- BigMode key handling was reworked and optimized.

### Fixed
- Effective default emulator resolution now works correctly in subnodes with inherited settings.
- Several memory leaks were removed, plus many smaller fixes.

### Removed
- EmuMovies scraper is temporarily disabled until the new API is rolled out.

---

## [0.0.6-alpha] - 2026-02-25

### Added
- added: new theme LivingRoom

### Changed
- changed: music files and manuals keep their original name now in the ui
- improved: performance in themes
- changed: updated Avalonia to 11.3.11

### Fixed
- fixed: crash after switching in themes between items with movies
- fixed: Comic Vine scraper now works

also a lot of small fixes and changes

---

## [0.0.5-alpha] - 2026-02-13

### Added
- added Crossfade for Wallpapers and Logos in the CoreApp
- added Crossfade for Wallpapers/Logos/Covers/Videos in BigMode (depending on used theme)
- added possibility to set portable Home/XDG usage in Settings
- added possibility to manually set steam and Heroic/GOG folders, hardened automatic import
- added possibility to set fallback Logos/wallpapers/videos through Node Settings

### Changed
- changed User Settings, added miscellaneous tab

### Fixed
- fixed a few bugs regarding theme settings in the Tree (System theme can now be set on root, Drag/Drop now takes duplicates into account)

---

## [0.0.4-alpha] - 2026-02-11

### Added
- „XDG‑Overrides for native titles“ + Working‑Directory‑Default

### Changed
- updated inheritance logic in Nodes/Emulators for env. variables and wrappers and made it more clear now
- optimized edit media dialog settings, made it simpler to set an emulator

### Fixed
- fixed bugs in Arcade-Theme (no sound in videos, centered logo view works now)
- added missing dependency in AppImage (bundled libidn for libvlccore)
- fixed a few small bugs

---

## [0.0.3-alpha] - 2026-01-30

### Added
- Added the ability to create 32‑bit / 64‑bit Wine/Proton prefixes
- Added Winetricks integration

### Changed
- Massive memory optimizations

### Fixed
- Fixed a large number of bugs
- Optimized and fixed the search function/view

---

## [0.0.2-alpha] - 2026-01-07

### Added
- Use relative paths internally when creating Wine prefixes.
- Allow changing the primary executable in media settings.
- Add wrapper support for emulators and native programs.
- Add per-item environment overrides.
- Allow marking items as favorites.
- Allow attaching documents/manuals to media items.

### Changed
- Optimized themes for better usability on smaller screens.
- Cleaned up themes.
- Some small changes to the ui.

### Fixed
- Correct handling of profiles when switching emulator/native.
- Theme cleanup after switchting between themes in BigMode.

---

## [0.0.1] - 2025-01-01

### Added
- Initial **BigMode** implementation with:
    - Arcade theme (cabinet + logo rail + per-item video preview).
    - System host theme with right-hand system layout slot (`SystemLayoutHost`).
    - Default system layout sub-theme (system video preview on the right).
- Node settings:
    - Per-node artwork (logo, wallpaper, video) based on `MediaNode.Assets`.
    - Per-node BigMode theme selection (`ThemePath`).
    - Per-node system preview theme selection (`SystemPreviewThemeId`).
- LibVLC-based video preview pipeline:
    - Main preview channel (`MainVideoSurface`) for per-item/system videos.
    - Secondary background channel (`SecondaryVideoSurface`) for theme-level videos.
- Gamepad navigation in BigMode (up/down/left/right/select/back).
- Attract mode support (idle timer that spins through games after inactivity).

### Changed
- (none yet)

### Fixed
- (none yet)

---

## Versioning

- `0.0.x` – early alpha versions, APIs and themes may change at any time.
- Once the project stabilizes, versions will move to `1.0.0`, `2.0.0`, etc.

---

[Unreleased]: https://github.com/Dark574/Retromind/compare/v0.1.5-alpha...HEAD
[0.1.5-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.5-alpha
[0.1.4-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.4-alpha
[0.1.3-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.3-alpha
[0.1.2-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.2-alpha
[0.1.1-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.1-alpha
[0.1.0-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.1.0-alpha
[0.0.9-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.9-alpha
[0.0.8-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.8-alpha
[0.0.7-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.7-alpha
[0.0.6-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.6-alpha
[0.0.5-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.5-alpha
[0.0.4-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.4-alpha
[0.0.3-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.3-alpha
[0.0.2-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.2-alpha
[0.0.1]: https://github.com/Dark574/Retromind/releases/tag/v0.0.1
