# Cinematic

Cinematic combines a full-screen wallpaper with a smaller floating video preview
at the upper right, a compact identity overlay at the lower left and a shallow
horizontal cover shelf. It uses charcoal shading and warm gold selection markers.
The wallpaper remains dominant, distinguishing the layout from HorizontalRow's
large media preview and Prism's separate framed preview and information layout.
There is no permanent theme wordmark, large information panel or duplicate title
in the footer. Existing year/genre metadata and at most three description lines are
shown; the stored description remains complete. Ratings and new actions are not used.
The selected item's existing favorite status appears as a gold star and localized
Favorite label beside the metadata. The indicator stays hidden for non-favorites,
categories and empty selections; it is a status display, not an editing control.

A compact achievement summary shows the shared provider title, unlocked/total
counts and a slim progress bar for Steam, GOG and RetroAchievements. Loading,
Hardcore/provider details and error/cache status use the central progress view model.

## Selecting the theme

Choose `Cinematic/theme.axaml` in the root BigMode setting or a category's theme
dropdown. Each opened category chooses its own theme: assigning Cinematic to the
virtual root does not assign it to its children. Discovery, packaging and portable
theme synchronization follow the existing folder conventions. No fonts, artwork,
players or other dependencies are bundled with this theme.

## Artwork and preview layers

The full-screen scene has explicit stacking:

1. `BackgroundVisual`: subdued cover fallback, screenshot fallback, media wallpaper
   and selected-node wallpaper.
2. `HeroShade`: a relative vertical gradient, transparent over the upper 40% and
   progressively darker around the lower identity/shelf area.
3. The transparent QHD stage: the floating `VideoSlot` first, then the navigation
   label, lower-left identity/actions, cover shelf and existing system-menu hint.
   The video stays above the global gradient. An additional gradient is confined
   to the identity overlay, clear of the video, rather than dimming the full left side.

Media wallpaper binds directly to `ActiveWallpaperPath`, using the existing
`AssetResolver` policy, including enabled source-node fallbacks. The resolver
prefers an item's wallpaper over its screenshot. `ActiveScreenshotFallbackPath`
is only populated when the central wallpaper selection is empty. A selected node
uses its own `PrimaryWallpaperAbsolutePath`, independently of the node's fallback
switch for its children. No competing artwork resolver is introduced.

The cover backdrop is a quiet, separate 640-pixel fallback under the main artwork.
The gradient remains when all artwork is missing. Missing covers use existing title
or node-logo fallbacks; captions and the hero title also identify unreadable images.

`VideoSlot` sits at the upper right of the QHD stage, bounded by **950 × 535**, with
**48** units of both top and right margin. Its width follows the
active frame's aspect ratio: approximately **713.3 × 535** for 4:3, and the full
950-unit width for 16:9. Thus the visible video's right edge keeps the same margin,
instead of centering narrower footage inside a fixed 950-unit slot. The small
`VideoPreviewWidthConverter` only reads existing main-surface dimensions; it does
not change playback or create a player. `MainVideoFrameRevision` updates the width
when the first frame becomes ready, including when the active index is unchanged.
Until dimensions are known, the slot uses its maximum width.

The slot has no background, frame, title bar, controls or persistent shadow. The left
identity overlay ends before the preview begins, including its local reading shade. The host
attaches its existing `CrossfadeVideoSurfaceControl`, with both main video surfaces,
active-surface selection and fade duration. The shared control's existing
`IsVideoOverlayVisible` binding owns visibility; the slot adds no second gate based
on `MainVideoHasContent`. Video still uses `Uniform`, without stretching or cropping.
The aspect-fitted slot eliminates the extra horizontal margin for 4:3 footage.
When the shared preview is hidden or absent, the transparent slot paints nothing;
there is no black window left on the wallpaper. Existing fades, cancellation and
the two main surfaces remain host/VM owned. No secondary player/channel is enabled.

## Correction of the first prototype

**Missing wallpaper:** The original media wallpaper binding already used
`ActiveWallpaperPath`; it did not prefer a missing screenshot over a supplied item
wallpaper. The full-screen `ShelfShade` used `StartPoint="0,0" EndPoint="0,1"` and an
opaque final color. Avalonia interprets coordinates without `%` as absolute pixels
([Avalonia 12.1.4 parser](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/RelativePoint.cs#L157-L181)).
The gradient therefore reached opaque black after one pixel and painted over the
wallpaper almost everywhere. All Cinematic gradient endpoints now use percentages.
The upper hero remains unshaded and the extra reading shade is local to the identity.

**Audible but invisible video:** The same opaque `ShelfShade` was also above the
video slot, so a correctly attached video surface could play audio and frames while
its image was covered. The slot name and existing host attachment path were already
correct. The corrected gradient removes this obstruction for video as well as for
wallpaper. Artwork fills the actual viewport; the floating video is now within the
scaled foreground, above the global reading gradient. The redundant slot
visibility binding was removed so the host alone controls preview visibility.

The user has since confirmed that wallpaper and video display correctly in the
AppImage. Version 0.1.3 also fits the floating slot to the active frame's width so
4:3 and 16:9 footage share a consistent right margin. The updated alignment and
audio/video transitions still need a new graphical AppImage check.

## Navigation, lifecycle and performance

- `NavigationPresentation="MixedEntries"` uses one `ListBox` bound to
  `NavigationEntries`, `SelectedEntry` and `SelectedEntryIndex`. Category cards carry
  a folder/Open badge. The existing Open and Start actions call `PlayCurrentCommand`.
  Filtering, Back, empty levels and per-location restore use the shared session.
- Controller, keyboard and page navigation remain host/VM owned. Home is supported
  when enabled in settings. The host supplies Home, the empty-state message and
  system menu; the hint reflects configured bindings. Existing Attract Mode selects
  visible direct media after 75 idle seconds, without opening nodes or launching.
- The shelf uses a horizontal `VirtualizingStackPanel`, `CacheLength="0.5"` and the
  existing animated centering behavior. Selection changes opacity, frame color and
  an underline; it neither resizes cards nor replaces the entry collection. Generic
  host selection effects are disabled on this list; list guardrails stay enabled.
- Covers preserve their proportions with `Uniform`. The shared cache decodes them
  at width 360 and card logos at 320. Hero decoding is bounded at 1920, including on
  4K screens; it trades native 4K detail for predictable browsing cost. Hero loads
  wait 220 ms, logos 160 ms. Existing cancellation, caching and serial decoding apply.

## Achievement summary and layout stability

The summary binds only to `BigModeViewModel.RetroAchievementsProgress`, using the
same `Title`, `SummaryText`, `ProgressValue`, `ProgressMaximum`, `HasProgress`,
`IsLoading`, `HardcoreSummaryText`, `StatusText` and `ShowStatus` contract as Default
and Prism. The complete summary follows `IsVisible`; its parent also requires media
selection, so category nodes never display progress. Initial loading/failed requests
without a known achievement set remain hidden, as required by the shared visibility
contract. A refresh of known progress can show the indeterminate bar and status.
No theme code requests progress or loads badges.

At QHD, the 956 × 64 summary starts 224 units to the right of the existing action
row (screen X=320), beside Start and above the cover shelf. A `Canvas` inside that
row contributes zero desired size. Its fixed summary grid can therefore appear,
disappear or change state without moving the title, metadata, three-line description
or actions. The existing gap below the description remains; the small overflow below
the action fits in the existing bottom margin above the shelf. No extra empty row
or panel is reserved when the summary is hidden. The video remains clear to the right.
The host-owned detail browser and configured Details key/button remain unchanged.

## Scaling

Artwork fills the available viewport at any aspect ratio. Video and UI share a finite
**2560 × 1440** stage with uniform scaling:

| Display | Maximum video slot | Top margin | Right margin |
| --- | --- | --- | --- |
| 1920 × 1080 | 712.5 × 401.25 | 36 | 36 |
| 2560 × 1440 | 950 × 535 | 48 | 48 |
| 3840 × 2160 | 1425 × 802.5 | 72 | 72 |

For 4:3 frames the slot widths are approximately 535, 713.3 and 1070 respectively;
the heights and right margins above stay the same.

On other aspect ratios, this stage fits with empty margins while the wallpaper still
fills the display. The finite stage bounds preserve virtualization.
The shelf is 338 reference units high; the compact identity is anchored above it.
Logos fit their natural aspect ratio inside a left-aligned `Viewbox`, capped at
100 units high and 760 wide, so narrow logos do not center inside an oversized image
area. Long titles wrap and can scale down inside their own bounded area. Desktop
display scaling can reduce the available logical resolution.

## Validation and AppImage checklist

C#/xUnit tests in `CinematicThemeTests` cover discovery/metadata, public binding
paths, canonical list bindings, image budgets, central wallpaper resolution with
and without owner fallbacks, selected-node artwork, relative gradient geometry,
transparent upper hero, explicit layer order, actual shared-video slot attachment,
surface bindings, floating slot bounds/margins, first-frame width updates, the same
right edge for 4:3 and 16:9, `Uniform` scaling and visibility changes without a
persistent background/shadow. Achievement regression checks compare identity-layout
bounds with the summary hidden/visible/hidden, using the real theme containers and
fixed layout probes without a font/windowing backend. They also verify the shared
progress bindings, media-only guard and absence of extra actions. Existing mixed-navigation
tests include Cinematic for protection, reachability, empty levels and restore.
The theme-specific Python test suite has been removed.

No build is performed as part of this correction. Newly added/modified test methods
require a newly compiled Release test assembly before they can be executed. After
that compilation, this command executes the suite without another build:

```bash
dotnet test tests/Retromind.Tests/Retromind.Tests.csproj --configuration Release --no-build --no-restore
```

Static checks:

```bash
xmllint --noout Themes/Cinematic/theme.axaml
git diff --check
```

Graphical checks still required in the user's Linux AppImage:

- QHD first, then Full HD, 4K and a different aspect ratio: full-screen artwork,
  readable lower-left text, complete long titles, shelf/footer spacing.
- Media with only wallpaper, wallpaper plus missing screenshot, inherited owner
  wallpaper, and selected nodes with their own artwork; then missing/broken assets.
- Floating video image and sound together, including 16:9 and 4:3 footage, A/B preview
  changes, no-video selections and hide/stop transitions revealing only the wallpaper.
  Check top/right margins, absence of text overlap, scaling, clipping and frame pacing.
- Fast mixed-level controller/keyboard/page navigation, empty/protected categories,
  Back/selection restore, Home, system menu, Attract Mode and active-video theme swaps.
- Games with/without Steam, GOG and RetroAchievements progress: unchanged title,
  metadata and description positions, clear description/shelf spacing, loading,
  Hardcore and error/cache status, no node progress, and the configured Details action.
