# Navigator

Navigator is a compact library browser: a vertical list on the left and a fixed
artwork/video preview with quiet information rows on the right. The dark slate
background and restrained teal selection keep attention on browsing. Artwork
stays inside the preview instead of filling the screen.

Navigator is the default for new root settings and library nodes. Saved theme
assignments remain unchanged; the previous Default theme stays selectable and
serves as a compatibility fallback if Navigator is unavailable.

## Layout and scaling

The finite 2560 × 1440 design surface uses a 36:64 split with a gap between the
columns. Around eleven rows fit below the breadcrumb. Each row has a small
aspect-preserving cover, a title of up to two lines and optional existing genre
information. Favorites have a gold star directly after the title. Nodes have a
folder fallback and an Open label.

A uniform Viewbox scales the whole surface to Full HD (0.75×) or 4K (1.5×).
Other aspect ratios retain the composition with unused margins, rather than
stretching images or cropping navigation. The preview and information rows use
a Grid, with star-sized columns and a remaining-height preview. Title/logo,
metadata, a description viewport showing five lines and achievement summary occupy separate rows;
missing data never moves another section. The empty achievement row is small
and has no visible panel or frame.

## Navigation

`NavigationPresentation="MixedEntries"` binds directly to `NavigationEntries`,
`SelectedEntry` and `SelectedEntryIndex`. Nodes precede directly contained media.
Activation, Back, controller/keyboard input, page navigation, Home, restoration,
system menu and Attract Mode use the existing BigMode host/session.

`NavigationBreadcrumb` is a display-only projection of the session's opened
nodes. Its leading ellipsis preserves the current location in long paths.
Highlighting rows neither changes the path nor rebuilds the list. The vertical
`VirtualizingStackPanel` has a half-viewport cache; shared centering follows the
selection. Host selection transforms are disabled, so rows keep their dimensions.

## Preview

The preview layers the selected cover, existing screenshot fallback and resolved
wallpaper below the central host-owned `VideoSlot`. Node selections use their
own artwork. Missing artwork leaves a neutral dark surface, without a placeholder
graphic. Preview images retain their previous image through the load debounce
instead of clearing immediately on selection changes. Shared asynchronous
image loading, bounded decode widths and load delays avoid unnecessary decodes
while browsing quickly.

`VideoStretchMode="Uniform"` preserves 4:3 and 16:9 video proportions within the
same fixed preview. For entries without video, the wallpaper fills the preview.
`MainVideoHasContent` hides all artwork as soon as a target preview is known,
including the debounce and first-frame wait; `IsVideoOverlayVisible` additionally
keeps artwork hidden during an outgoing video fade. No new wallpaper flashes
before a video starts, and 4:3 side margins use the same neutral dark color as
the rest of the theme.
While the artwork layer is hidden, its image URLs are set to null through
`ObjectConverters.WhenTrue`, cancelling pending loads and clearing decoded
control buffers. Returning from video to still artwork therefore cannot expose
a retained wallpaper from the previous entry. Direct still-to-still changes
continue to retain their outgoing image through the loading delay.
There is no extra video player, opaque overlay or gradient above the video.
Host transitions, cancellation and preview debouncing remain unchanged.

## Description

The fixed 170-pixel description viewport initially shows about five lines. The
complete text wraps without a line limit or ellipsis. The existing
`ScrollViewerBehaviors.AutoVerticalScroll` starts after ten seconds, moves at
eight design pixels per second, pauses for two seconds at the bottom and then
returns to the top with another ten-second reading pause. Short descriptions
do not scroll. Binding the restart token to `SelectedEntry` immediately resets
the viewport and delay on selection changes, even for identical descriptions.
The viewport does not consume pointer or keyboard navigation input.

## Achievements

The noninteractive summary uses only `RetroAchievementsProgress`, supporting the
existing Steam, GOG and RetroAchievements sources. It shows the provider title,
unlocked/total count, a thin progress bar and existing loading, status/error and
hardcore information. Visibility follows the shared `IsVisible` state and is
additionally guarded by media selection. No achievements are shown for nodes.
The host's achievement details view and configured details button remain intact.

## Validation and remaining graphical checks

C#/xUnit coverage checks discovery/runtime loading, canonical mixed selection and
activation, bounded vertical virtualization, breadcrumb updates, shared video
attachment and fixed information positions at Full HD, QHD, 4K and ultrawide.

An AppImage practice test is still required for native video playback and
crossfades, rapid controller scrolling, long localized paths/titles, missing
artwork, each achievement provider and theme/Home/Attract transitions. Judge text
size and column spacing first at 2560 × 1440, then at the other target sizes.
