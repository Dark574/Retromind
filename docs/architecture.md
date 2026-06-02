# Retromind Architecture (high-level)

This document summarizes how Retromind is structured today and where core behavior lives.

## Tech stack
- UI: Avalonia (`12.x`) with MVVM (`CommunityToolkit.Mvvm`)
- Language/runtime: C# on `.NET 10` (`net10.0`)
- DI: `Microsoft.Extensions.DependencyInjection`
- Video preview pipeline: `LibVLCSharp` (BigMode)
- Audio/SFX helpers: external players (`ffplay`, `sidplayfp`) via services

## Runtime profile and startup
- `Program.Main` configures runtime behavior before Avalonia starts:
  - `--bigmode` startup mode
  - `--avalonia-platform` is currently constrained to `x11` on Linux (`wayland`/`auto` are intentionally disabled)
  - AppImage portable HOME/XDG redirection via `PortableEnvironment.ApplyPortableXdgPaths()`
  - mandatory LibVLC initialization (`Core.Initialize()`)
- `App.OnFrameworkInitializationCompleted` then:
  - synchronizes portable themes (`AppPaths.EnsurePortableThemes()`)
  - bootstraps settings first
  - builds final DI container
  - creates `MainWindow` and triggers async `MainWindowViewModel.LoadData()`

## Repository map (logical)
- `Views/`: Avalonia views, including host-level behavior (`BigModeHostView`)
- `ViewModels/`: orchestration and state; `MainWindowViewModel` is split into partial files by concern
- `Models/`: persisted domain and settings (`MediaNode`, `MediaItem`, `AppSettings`, etc.)
- `Services/`: persistence, launcher, import/store import, scraping, audio, themes
- `Helpers/`: portability, path safety, converters, environment sanitizers, UI helpers
- `Extensions/`: `ThemeProperties` attached-property surface for runtime themes
- `Themes/`: shipped editable runtime themes
- `Resources/`: localized strings

## Main UI architecture (desktop mode)
Main window is a layered shell:
1. Background/wallpaper layer
2. Three-column working layout:
   - left: tree (`RootItems`, `SelectedNode`)
   - center: active content VM (`SelectedNodeContent`, usually `MediaAreaViewModel`)
   - right: details for selected media item
3. Full-screen overlay slot (`FullScreenContent`) used by BigMode host

`MainWindowViewModel` is the core orchestrator:
- owns tree/content selection, command wiring, persistence triggers, and lifecycle cleanup
- rebuilds center content through a cancelable `UpdateContent()` pipeline
- keeps content updates race-safe (CTS + TCS), with background item collection and single UI-thread commit
- preserves and restores search/filter UI state when switching between node view and global search

## Persistence model
Persisted app/library data is portable under `AppPaths.DataRoot` (AppImage directory or app base directory).
Exception: secrets for native store auth (e.g. GOG refresh token) are stored via host secret store (`ISecretStore`),
not in `DataRoot`.

### Library (`retromind_tree.json`)
- service: `MediaDataService`
- atomic write strategy: temp -> backup -> replace
- IO is serialized with `SemaphoreSlim`
- `MainWindowViewModel` tracks dirty state (`_isLibraryDirty`, versioned), performs debounced saves, snapshots on UI thread, serializes in background

### Settings (`app_settings.json`)
- service: `SettingsService`
- same atomic temp/backup strategy with serialized IO
- corrupt settings are quarantined and fallback restore from `.bak` is attempted
- sensitive scraper secrets are encrypted/decrypted via `SecurityHelper`

## Path and portability contract
- persisted file paths are expected to be DataRoot-relative when possible
- path resolution for runtime assets/documents/themes uses `AppPaths.ResolveDataPathInsideRootOrEmpty`
- escaping `DataRoot` is intentionally blocked (`TryResolveDataPathInsideRoot`)
- this boundary is central for portable use and for avoiding accidental absolute-path drift

## Theme subsystem
Themes are external runtime XAML loaded through `ThemeLoader`:
- resolves relative theme paths against portable `ThemesRoot`
- parses theme XAML at runtime and applies theme base path per view instance
- caches XAML text with LRU to reduce repeated file IO/parse overhead
- exposes theme metadata, visual tuning, selection effects, typography, video options, attract-mode options, etc. via `ThemeProperties` attached properties

### Portable theme sync/update at startup
`AppPaths.EnsurePortableThemes()` implements best-effort shipped-theme sync:
- first-time copy of missing top-level themes
- manifest-based update gate via `.retromind-theme.json`
- manifest recovery when file is missing/corrupt but theme content still matches shipped version
- restoration of missing theme directories (any directory containing `theme.axaml`)
- local theme modifications are preserved (no forced overwrite when hashes differ)

## BigMode architecture
BigMode is an overlay workflow with clear host/VM split:

### Host (`BigModeHostView`)
- theme root attachment/swapping (`SetThemeContent`)
- shared video control attachment to theme-defined slots
- system-host mode with per-system subtheme loading (`Themes/System/<id>/theme.axaml`)
- subtheme cache with LRU
- theme guardrails/tuning for list behavior and selection visuals
- global cursor idle hide/show behavior (mouse-only)

### ViewModel (`BigModeViewModel`)
- navigation state (categories/items), selection memory, and robust restore from persisted settings
- node-aware artwork resolution and fallback overrides (logo/marquee etc.)
- dual preview surfaces with crossfade and defensive playback sequencing
- secondary background video channel support
- attract mode (theme-driven idle navigation)
- mirrors final BigMode selection back into core app settings on exit

## Search architecture
Global search uses a dedicated `SearchAreaViewModel`:
- debounced, cancelable background evaluation
- scope selection by node IDs
- parental-filter-aware visibility
- row grouping for large virtualized result grids
- shared filter state (text/favorites/status/year) coordinated by `MainWindowViewModel`

## Import and metadata flow
- `ImportService`: recursive local file import with multi-disc grouping/labeling
- `StoreImportService`: Steam import via `steamapps` manifest scan (`appmanifest_*.acf`) + Heroic Epic discovery
  (`installed.json`) with auto/manual paths and portable-home awareness in AppImage mode
- Native store-provider integration under `Services/Stores/` (GOG auth/library/install flow wired via `GogProvider`)
- `MetadataService`: scraper-provider factory + provider caching + connect gating
- scraper providers implement `IMetadataProvider` and are selected via configured scraper profile

For detailed GOG-native status and file map, see `docs/gog-provider.md`.

## Launch pipeline
`LauncherService` resolves and executes media launches:
- supports `Native`, `Emulator`, and `Command` media types
- launch plan layering: item launcher -> emulator config -> wrapper chain
- supports multi-file launch decisions (including playlist mode)
- supports merged environment overrides (node/emulator/item)
- handles Wine/Proton/UMU prefix setup and compatibility environment shaping
- sanitizes host/runtime environment in AppImage/Flatpak/store-related cases
- session tracking updates playtime/playcount after launch

## Parental control as cross-cutting concern
Parental behavior is not isolated to one screen:
- tree visibility recalculation (`IsVisibleInTree`) on lock/protection changes
- current content refresh and fallback node selection when active node becomes hidden
- search view respects parental filtering
- node/item protection state propagation and auto-protect recalculation is debounced

## Media model notes
- `MediaNode` and `MediaItem` both support asset collections and active-asset overrides
- node-level fallback toggles control whether node artwork participates in item display resolution
- `MediaItem.MediaType` models launch strategy (`Native`, `Emulator`, `Command`), not content taxonomy

## Extending the app safely
When adding features, preserve these invariants:
- keep persisted paths portable and inside `DataRoot`
- keep library/settings writes atomic and non-concurrent
- avoid UI-thread blocking in import/scrape/search/rebuild paths
- keep BigMode host/VM responsibilities separated
- do not bypass launcher/environment sanitization for host helper processes
