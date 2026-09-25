# Prism

Prism is a cinematic BigMode theme designed to put the library's own media in
the foreground. It combines a large video or screenshot stage with a centered
horizontal cover carousel, compact metadata, and resilient artwork fallbacks.

## Recommended setup

- Select `System` as the BigMode theme for the main library level.
- Assign the appropriate system-preview theme to each top-level platform node.
- Assign `Prism` as the BigMode theme for the platform nodes whose games should
  use this layout.

Prism does not ship third-party artwork. Wallpapers, screenshots, covers,
logos, and videos are loaded from the selected Retromind item or node.

The background crossfades independently from the shorter foreground
transitions. A screenshot is decoded only when no wallpaper is available, so
rapid navigation does not load two full-screen images for the same selection.
The horizontal carousel virtualizes the complete game list so only visible cards
are realized while native mouse selection remains available.
Item and category logos use the same delay and fade duration as their wallpaper,
keeping both artwork transitions visually synchronized.
The center-stage cover, screenshot, and new video frame use the same fade duration.
Previous preview frames are cleared immediately instead of being retained during
that transition.

## Dynamic accents

Prism derives a primary and secondary accent color from the selected artwork
and transitions between palettes as the selection changes. Artwork analysis is
debounced and cached so rapid carousel navigation does not decode every
intermediate image. Cyan and violet remain the fallback palette when no useful
artwork color can be extracted.
