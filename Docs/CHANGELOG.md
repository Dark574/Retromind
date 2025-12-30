# Changelog

All notable changes to **Retromind** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),  
and this project (aims to) adhere to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- (planned) More system sub-themes and BigMode layouts.
- (planned) Additional media types (movies, books, comics, ...).

### Changed
- (planned) General polish for BigMode navigation and theming.

### Fixed
- (planned) Minor layout glitches and video edge cases.

---

## [0.0.1] - 2025-01-xx

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
- Once the project stabilizes, versions will move to `0.1.0`, `0.2.0`, etc.

---

[Unreleased]: https://github.com/<your-account>/Retromind/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/<your-account>/Retromind/releases/tag/v0.0.1