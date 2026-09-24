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

## Dynamic accents

Prism derives a primary and secondary accent color from the selected artwork
and transitions between palettes as the selection changes. Artwork analysis is
debounced and cached so rapid carousel navigation does not decode every
intermediate image. Cyan and violet remain the fallback palette when no useful
artwork color can be extracted.
