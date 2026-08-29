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
  - Linux defaults to X11/XWayland; `--avalonia-platform=wayland` opts into Avalonia's experimental native
    Wayland backend with X11 initialization fallback, while `x11` and `auto` retain the stable X11 path
  - AppImage portable HOME/XDG redirection via `PortableEnvironment.ApplyPortableXdgPaths()`
  - mandatory LibVLC initialization (`Core.Initialize()`)
- Release AppImages use checksum-pinned official `appimagetool` and static Type-2 runtime artifacts, removing
  the host `libfuse2` dependency. Their versioned filename, embedded GitHub Releases update information, and
  matching `.zsync` metadata are produced together and must not be renamed after the build.
- `App.OnFrameworkInitializationCompleted` then:
  - synchronizes portable themes (`AppPaths.EnsurePortableThemes()`)
  - bootstraps settings first
  - builds final DI container
  - creates `MainWindow` and triggers async `MainWindowViewModel.LoadData()`
  - suppresses native Wayland server-side decoration negotiation before the first surface is created,
    then keeps the startup surface transparent until the compositor confirms the requested
    maximized/fullscreen state; together these prevent a brief decorated normal-window flash

## Repository map (logical)
- `Views/`: Avalonia views, including host-level behavior (`BigModeHostView`)
- `ViewModels/`: orchestration and state; large view models such as `MainWindowViewModel` and
  `SettingsViewModel` are split into partial files by concern
- `Models/`: persisted domain and settings (`MediaNode`, `MediaItem`, `AppSettings`, etc.)
- `Services/`: persistence, launcher, import/store import, scraping, audio, themes
- `Helpers/`: portability, path safety, converters, environment sanitizers, UI helpers
- `Extensions/`: `ThemeProperties` attached-property surface for runtime themes
- `Themes/`: shipped editable runtime themes
- `Resources/`: localized strings
- `tests/Retromind.Tests/`: xUnit tests for deterministic, high-risk application behavior

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
- opens the library-statistics dialog and translates its navigation/filter requests back into the existing
  node-content or global-search workflows
- coordinates explicit item moves between nodes through a single-selection tree or tile-to-tree drag-and-drop;
  both paths share the same confirmation and move transaction, while inherited launch settings are resolved
  from the new parent without copying node defaults onto the item

## Persistence model
Persisted app/library data is portable under `AppPaths.DataRoot` (AppImage directory or app base directory).
Exception: secrets for native store auth (e.g. GOG refresh token) are stored via host secret store (`ISecretStore`),
not in `DataRoot`.

### Library (`retromind_tree.json`)
- service: `MediaDataService`
- atomic write strategy: temp -> backup -> replace
- IO is serialized with `SemaphoreSlim`
- `LibraryChangeTracker` owns versioned dirty tracking and debounced saves
- bound collections are snapshotted on the UI thread and serialized in the background
- dirty state is cleared only after a successful write; save failures remain dirty and are surfaced to the user
- a valid empty primary library is authoritative; the backup is used only when the primary file is missing,
  unreadable, or invalid

### Settings (`app_settings.json`)
- service: `SettingsService`
- same atomic temp/backup strategy with serialized IO
- corrupt settings are quarantined and fallback restore from `.bak` is attempted
- save failures propagate to the caller and are surfaced to the user
- sensitive scraper secrets are encrypted/decrypted via `SecurityHelper`
- the settings dialog edits a detached working copy; **Save** commits it, while Cancel or closing the
  window discards it
- title sorting remains a live preview while the dialog is open and is restored if the dialog closes
  without saving

## Path and portability contract
- persisted file paths are expected to be DataRoot-relative when possible
- path resolution for runtime assets/documents/themes uses `AppPaths.ResolveDataPathInsideRootOrEmpty`
- escaping `DataRoot` is intentionally blocked (`TryResolveDataPathInsideRoot`)
- generic Linux filesystem identity and node asset-directory operations are case-sensitive
- this boundary is central for portable use and for avoiding accidental absolute-path drift
- provider-specific Windows metadata may use a case-insensitive, segment-by-segment fallback only after
  containment in the selected install root has been established
- GOG `Store.InstallPath` values inside DataRoot are stored DataRoot-relative when portable paths are
  enabled; the shared GOG resolver also supports legacy and intentionally external absolute paths
- destructive store operations have an additional ownership boundary: GOG install directories must not
  be dangerous roots, must not traverse symbolic links, and must contain a matching
  `.retromind-install.json` marker before recursive deletion
- moving an item between nodes transfers its referenced assets through a staging transaction; failures roll
  files and the collection assignment back, while assets shared with another item or node are copied instead
  of removing the shared source
- synchronized store nodes accept only items with the matching provider identity and reject duplicate store
  game IDs; moving an item out remains possible but warns that a later full store sync may add it again

## Theme subsystem
Themes are external runtime XAML loaded through `ThemeLoader`:
- resolves relative theme paths against portable `ThemesRoot`
- parses theme XAML at runtime and applies theme base path per view instance
- caches XAML text with LRU to reduce repeated file IO/parse overhead
- exposes theme metadata, visual tuning, selection effects, typography, video options, attract-mode options, etc. via `ThemeProperties` attached properties
- user-visible runtime-theme text is exposed through localized `ThemeStrings` resource properties

### Portable theme sync/update at startup
`AppPaths.EnsurePortableThemes()` implements best-effort shipped-theme sync:
- first-time copy of missing top-level themes
- manifest-based update gate via `.retromind-theme.json`
- manifest recovery when file is missing/corrupt but theme content still matches shipped version
- restoration of missing theme directories (any directory containing `theme.axaml`)
- local theme modifications are preserved (no forced overwrite when hashes differ)

## Image loading and cache

`AsyncImageHelper` owns asynchronous bitmap loading, downsampling, cancellation, and its shared LRU cache.
Cache entries are reference-counted while controls display them. The cache has a hard limit of 200 entries;
if every cached bitmap is currently in use, a newly loaded bitmap remains control-owned and uncached instead
of allowing the shared cache to grow beyond its limit.

## BigMode architecture
BigMode is an overlay workflow with clear host/VM split:

### Host (`BigModeHostView`)
- theme root attachment/swapping (`SetThemeContent`)
- shared video control attachment to theme-defined slots
- system-host mode with per-system subtheme loading (`Themes/System/<id>/theme.axaml`)
- subtheme cache with LRU
- shipped system themes currently include Default, C64, Amiga, PC, and SNES variants
- theme guardrails/tuning for list behavior and selection visuals
- global cursor idle hide/show behavior (mouse-only)

### ViewModel (`BigModeViewModel`)
- navigation state (categories/items), selection memory, and robust restore from persisted settings
- node-aware artwork resolution and fallback overrides (logo/marquee etc.)
- dual preview surfaces with crossfade and defensive playback sequencing; presentation state avoids stale
  frames and unnecessary restarts when multiple items share the same node fallback video
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
- saved search terms persist their associated favorites-only state as part of the saved filter behavior
- structured query matching includes `played:true` and `played:false` (alias: `started`); play evidence is
  defined centrally by `MediaPlayStateHelper` as a launch count, recorded play time, or last-played timestamp

## Library statistics
`LibraryStatisticsViewModel` calculates statistics on demand from the existing media tree; there is no
separate statistics database or persisted aggregate state.

- scope defaults to the entire visible library and can be changed to any selectable category including its
  descendants; categories are identified by their full tree path
- protected items are omitted while parental filtering is active
- summary values cover item count, total play time, launches, favorites, and play status
- rankings show the ten most-played and ten most-recently played items and can navigate back to the real item
- distributions group the ten largest values by category, platform, genre, release year, or status
- clickable summary cards reuse the normal filters rather than maintaining a second filtering implementation;
  category scope stays in `MediaAreaViewModel`, while library-wide results use `SearchAreaViewModel`
- “In progress” and “Never started” combine the normal `Incomplete` status with `played:true` or
  `played:false`, using the same `MediaPlayStateHelper` definition as the displayed counts

## Import and metadata flow
- `ImportService`: recursive local file import with multi-disc grouping/labeling
- `MultiDiscFileNameHelper`: shared recognition for separated `Disk`, `Disc`, `CD`, `Side`, `Part`, and
  `Scen` filename forms; `LauncherService` can consume generated playlists for grouped media
- `StoreImportService`: Steam import via `steamapps` manifest scan (`appmanifest_*.acf`) + Heroic Epic discovery
  (`installed.json`) with auto/manual paths and portable-home awareness in AppImage mode
- Native store-provider integration under `Services/Stores/` (GOG auth/library/install flow wired via `GogProvider`)
- `MetadataService`: scraper-provider factory + provider caching + connect gating
- scraper providers implement `IMetadataProvider` and are selected via configured scraper profile
- providers with expensive per-result calls expose optional preview/result enrichment
  capabilities; the manual dialog loads lightweight previews first and details only
  for the current selection, while bulk scraping enriches only the matched result
- the manual metadata dialog presents changed fields individually; artwork imports are additive and never
  silently replace existing files
- `ScraperMatchEvaluator` gates automatic bulk imports by normalized title similarity, optional platform/year
  signals, a minimum confidence, and a lead over the runner-up; ambiguous or unsafe results are logged but
  not imported

For detailed GOG-native status and file map, see `docs/gog-provider.md`.

## Launch pipeline
`LauncherService` resolves and executes media launches:
- supports `Native`, `Emulator`, and `Command` media types
- media items may intentionally have no launch file and act as catalog placeholders; the editor can add,
  replace, or clear launch files later
- launch plan layering: item launcher -> emulator config -> wrapper chain
- explicit test launches use the same launch plan while suppressing play count, last-played, and playtime updates
- supports multi-file launch decisions (including playlist mode)
- supports merged environment overrides (node/emulator/item)
- handles Wine/Proton/UMU prefix setup and compatibility environment shaping
- sanitizes host/runtime environment in AppImage/Flatpak/store-related cases
- session tracking updates playtime/playcount after launch
- GOG launch detection prefers local or account `playTasks` metadata and preserves its executable,
  arguments, and working directory before falling back to filesystem heuristics; this is important for
  installer-supplied DOSBox configurations

## Runner management

Runner definitions are stored in `AppSettings` and selected through inheritance from emulator to media item.
`SettingsViewModel.Runners` owns discovery, managed GE-Proton downloads, registration, usage/replacement, and
removal. A completed managed download is registered immediately, managed files require confirmation before
physical removal, and display order uses `MediaSortHelper.NaturalStringComparer` so numeric release segments
sort naturally (for example, GE-Proton9 before GE-Proton10).

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

## Automated tests

`Retromind.sln` includes the `tests/Retromind.Tests` xUnit project. The main SDK project explicitly excludes
`tests/**/*.cs` from its own default compile items, and selected internal contracts are exposed to the test
assembly through `InternalsVisibleTo` rather than widening the production API.

The suite targets both `GogInstallDirectorySafety`, because it guards recursive deletion, and the portable
path contract. Tests use unique `/tmp/retromind-tests-<guid>` roots, validate the exact target before cleanup,
and avoid following symbolic links. Covered cases include dangerous system/application roots, ownership
markers and symbolic links as well as Linux case sensitivity, path containment, prefix conversion,
idempotent migration, preservation of external paths, a persisted-library move from one DataRoot to another,
GOG uninstall resolution after such a move, and deterministic search-query behavior such as the shared
played/not-played semantics used by library-statistics filters.

Future coverage should stay risk-based and favor deterministic logic with low maintenance cost. The next
useful candidates are persistence fallback/write-failure behavior, multi-disc filename recognition, and
scraper matching decisions. UI tests should be added only where behavior cannot be tested below the Avalonia
view layer.

## Extending the app safely
When adding features, preserve these invariants:
- keep persisted paths portable and inside `DataRoot`
- keep library/settings writes atomic and non-concurrent
- never clear dirty state or report success after a failed persistence write
- never restore a non-empty backup over a valid empty primary library
- require explicit ownership evidence and symlink-safe containment before recursive deletion
- avoid UI-thread blocking in import/scrape/search/rebuild paths
- keep BigMode host/VM responsibilities separated
- do not bypass launcher/environment sanitization for host helper processes
- add focused regression coverage when changing destructive operations or other deterministic high-risk rules
