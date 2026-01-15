# Retromind Architecture (high-level)

This document gives contributors a quick overview of how Retromind is structured and how data flows through the UI.

## Tech stack
- UI: Avalonia (MVVM style)
- Language: C# (net10.0)
- Services: custom services + DI (Microsoft.Extensions.DependencyInjection)

## Folder structure
- `Views/`  
  Avalonia views (`*.axaml`) and minimal code-behind for UI-only behaviors.
- `ViewModels/`  
  UI state and orchestration (commands, selection, navigation, dialogs).
- `Models/`  
  Persisted domain objects: tree nodes, media items, assets, settings, scraper configs.
- `Services/`  
  IO and system integration: persistence (JSON), import, scraping, audio, launcher, controller, themes.
- `Helpers/`  
  Utilities: converters, randomization helpers, image loading/caching, layout panels.

## UI layout (3 columns)
Retromind uses a classic three-pane layout:

1) Left: TreeView (library structure)
- Bound to `MainWindowViewModel.RootItems`
- Selection is `MainWindowViewModel.SelectedNode`

2) Center: Media list / cover grid
- Bound to `MainWindowViewModel.SelectedNodeContent`
- Typically a `MediaAreaViewModel` that exposes a list of `MediaItem` and a `SelectedMediaItem`

3) Right: Details
- Bound to `SelectedNodeContent.SelectedMediaItem`
- Displays metadata and assets for the currently selected item

## Data flow: selection -> content
1. User selects a node in the tree.
2. `SelectedNode` changes in `MainWindowViewModel`.
3. `UpdateContent()` runs:
    - Collects items recursively for the selected node
    - Applies optional randomization (covers/wallpapers/music)
    - Creates a new `MediaAreaViewModel`
4. UI updates:
    - Center pane shows the cover/grid view
    - Right pane shows the details for `SelectedMediaItem`

## Persistence
### Library tree
- Stored as JSON in the app directory (portable):
    - `retromind_tree.json` (+ backup/temp)
- Service: `Services/MediaDataService`
- Write strategy: temp write -> atomic replace + backup (reduces corruption risk)

### Settings
- Stored as JSON in the app directory:
    - `app_settings.json`
- Service: `Services/SettingsService`
- Some sensitive fields are stored encrypted (portable, not “high security”)

## Assets (images/music/video)
- Each `MediaItem` and `MediaNode` can have a list of `MediaAsset`
- Paths are typically stored relative to the app directory to remain portable
- Image loading uses an async helper with caching to keep scrolling smooth

## BigMode
- BigMode is an overlay mode (full-screen content on top of the normal UI)
- It can be entered via a command or started via `--bigmode`

## Contributing tips
- Keep view code-behind minimal (UI-only). Put logic into ViewModels/Services.
- Avoid committing runtime data (`Library/`, `app_settings.json`, `retromind_tree.json`, secrets).
- Prefer small PRs: one behavior/change per PR with a short explanation and screenshots for UI changes.
- UI thread: do not call `Dispatcher.UIThread.*` directly; use `Helpers/UiThreadHelper` to keep UI-thread marshalling centralized and Models UI-agnostic.

## How to add a new media type (overview)

Retromind is designed to support multiple kinds of content (games, movies, books, comics, …).  
Content categories are **not** hard-coded in the `MediaType` enum. Instead, they are modeled via:

- The **library tree** (`MediaNode`) — e.g. top-level nodes like “Games”, “Movies”, “Books”.
- **Tags and metadata** on `MediaItem` — e.g. genres, series, platforms.
- Optional **per-node settings** — e.g. default emulator, BigMode theme, system preview theme.

Adding a new *content category* usually involves:

1. **Tree structure**
    - Create new nodes or folders in the left-hand tree (e.g. “Movies → Action”, “Comics → Manga”).
    - Optionally store a convention in node names or tags (e.g. “this subtree is for movies”).

2. **Metadata & tags**
    - Use existing fields like `Genre`, `Series` and `Tags` to further classify items.
    - If you need additional fields (e.g. “Writer” / “Artist” for comics), extend `MediaItem` with optional properties or use a flexible key/value structure.

3. **Import and scraping**
    - Extend import logic to place files into the correct nodes and assign appropriate tags.
    - Optionally add or extend scraper providers to fetch metadata that makes sense for the new category.

4. **UI integration**
    - Make sure the center cover grid and the right details pane display relevant fields for this category.
    - Keep labels and layout neutral where possible (e.g. “item” / “media” instead of “game”).

Note: The `MediaType` enum is reserved for **how** an item is launched:

- `Native`   → directly executable (binary, shell script)
- `Emulator` → launched via an emulator
- `Command`  → external command or URL protocol (e.g. `steam://`, `heroic://`)