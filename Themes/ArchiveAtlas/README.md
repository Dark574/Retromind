# Archive Atlas

Archive Atlas is a system-agnostic BigMode theme with a bright, curated
"museum catalog" look. It is designed to feel neutral across games,
films, books, and other media types.

## Highlights
- Warm paper background with subtle atlas glow and dotted texture
- Serif display title + clean sans body + mono labels
- Large video, screenshot, or category preview with a floating cover card
- Catalog-style details panel with couch-readable, auto-scrolling descriptions
- Virtualized index navigation for large libraries

## Fonts
This theme currently ships with:
- IBM Plex Mono (Regular)

Title/body fonts are resolved via system `Serif`/`Sans` families for now.
See the license files in `Fonts/`.

## Optional background video
If you want a subtle moving background, drop a video here:

`Themes/ArchiveAtlas/Videos/background_loop.mp4`

The theme already points to that path.

## Customization
You can tweak colors and sizes in `theme.axaml`:
- `PaperGradient`, `AtlasGlow` for background mood
- `AccentBrush` and `AccentSoftBrush` for highlights
- `Title` and `Mono` styles for typography
