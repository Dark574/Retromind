# Changelog

All notable changes to **Retromind** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),  
and this project (aims to) adhere to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

## Versioning

- `0.0.x` – early alpha versions, APIs and themes may change at any time.
- Once the project stabilizes, versions will move to `0.1.0`, `0.2.0`, etc.

---

[Unreleased]: https://github.com/Dark574/Retromind/compare/v0.0.2-alpha...HEAD
[0.0.1]: https://github.com/Dark574/Retromind/releases/tag/v0.0.1
[0.0.2-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.2-alpha
[0.0.3-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.3-alpha
[0.0.4-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.4-alpha
[0.0.5-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.5-alpha
[0.0.6-alpha]: https://github.com/Dark574/Retromind/releases/tag/v0.0.6-alpha