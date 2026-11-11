# Retromind Themes (BigMode) — Theme Author Guide

Source review: **2026-11-11**, .NET 10 / Avalonia 12.1.4. This is the general theme
API guide; individual layouts and their graphical checklists belong in each
theme's `README.md`.

Retromind supports **external `.axaml` themes** that are loaded **at runtime**.  
This allows anyone to create and publish themes without recompiling Retromind.
This applies to AXAML and assets using the installed application's theme API.
Adding or moving a C# theme extension requires rebuilding the application; copying
new AXAML into an older AppImage cannot supply a missing .NET type.

> **Trust boundary:** Runtime AXAML is not sandboxed. Avalonia can resolve public .NET types from loaded
> assemblies and may run their constructors while creating a theme. Retromind currently ships all available
> themes itself and does not provide an external theme distributor. If you manually add a third-party theme,
> treat it like an executable extension and use only sources you trust.

Theme files must remain below the portable `Themes/` directory and are limited to
1 MiB. Theme-local files resolved by the scoped asset/font helpers and sound/video
configuration must use relative paths inside that theme's own directory. Those
resolvers reject parent traversal, absolute paths and symbolic-link/reparse-point
paths. Library artwork bindings already return resolved data-root paths; they use
the library asset contract. `CrossfadeImage.Url` also supports the existing image
loader's HTTP sources. Runtime AXAML itself remains the trusted boundary above.

This guide covers:

- Theme structure and folder layout
- Conventions (slots / classes)
- `ThemeProperties` (metadata, sounds, tuning)
- Best practices, performance tips, and troubleshooting

## Quick start: creating a new BigMode theme

If you just want to get started quickly:

1. **Copy an existing theme folder**
    - For a mixed library list, start with `Themes/Default/`.
    - Duplicate it under a new name, e.g. `Themes/MyTheme/`.

2. **Rename the main file**
    - Keep the filename `theme.axaml` in your new folder so the loader finds it:
        - `Themes/MyTheme/theme.axaml`

3. **Update metadata on the root control**
    - Change `ThemeProperties.Name`, `Author`, `Version` and any tuning values:
   ```xml
   <UserControl xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="using:Retromind.ViewModels"
                xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
                x:DataType="vm:BigModeViewModel"
                Background="Black"
                ext:ThemeProperties.Name="MyTheme"
                ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
                ext:ThemeProperties.Version="0.1.0"
                ext:ThemeProperties.NavigationPresentation="MixedEntries">
       <!-- Your layout -->
   </UserControl>
   ```

4. **Keep the DataContext contract**
    - The theme root must assume `DataContext` is a `BigModeViewModel`:
        - Use `x:DataType="vm:BigModeViewModel"` for strong-typed bindings.

5. **Place your main video slot**
    - Add a named element that should host the main video overlay:
   ```xml
   <Border x:Name="VideoSlot"
           Width="800"
           Height="450"
           Background="#000000">
       <!-- The host replaces this slot's content; put fallbacks in a sibling layer. -->
   </Border>
   ```
    - Optionally configure `ThemeProperties.VideoSlotName` if you do not want to use `VideoSlot` as the name.

6. **Bind to `BigModeViewModel` properties**
    - Use `NavigationEntries`, `SelectedEntry` and `SelectedEntryIndex` for library navigation.
      Media/node properties remain available for type-specific preview details.
    - Show covers, logos and text using the properties exposed by the view model.

7. **Test in Retromind**
    - Start Retromind with `--bigmode`.
    - Select your theme in the node settings (“BigMode theme” dropdown).
    - Adjust spacing, animations and typography via `ThemeProperties` as needed.

For a detailed explanation of all available hooks, video channels and `ThemeProperties`, read the sections below.

---

## 1) What is a theme?

A theme is an Avalonia XAML file (typically a `UserControl`) loaded at runtime.  
Retromind sets the theme root’s `DataContext` to `BigModeViewModel`, so you can bind directly to it.

### Minimal theme skeleton
```xml 
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             xmlns:helpers="clr-namespace:Retromind.Helpers;assembly=Retromind"
             xmlns:resources="clr-namespace:Retromind.Resources;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             Background="Black">
    <!-- Your layout -->
</UserControl>
```

Short AXAML fragments below assume the namespace declarations from this skeleton.

---

## 2) Where do themes live? (recommended)

Themes typically live under:

- `Themes/<ThemeName>/theme.axaml`

Example:

- `Themes/Wheel/theme.axaml`

Ship your theme as a folder so users can install it by copying the directory.
The application discovers top-level folders containing `theme.axaml`. The virtual
library root chooses its theme through `AppSettings.RootBigModeThemePath`; real
nodes use their own `ThemePath`. Missing/invalid node themes fall back to Default,
without inheriting their parent's theme.

At startup, shipped themes are synchronized to the portable theme directory.
Missing themes are restored; manifest/hash checks update unchanged shipped copies
and preserve local modifications. An edited portable copy may therefore keep an
older AXAML file after an application update.

Recommended structure:
- `Themes/MyTheme/theme.axaml`
- `sounds/navigate.wav, confirm.wav, cancel.wav`
- `images/ ...`

---

## 3) DataContext & bindings

The host automatically sets:

- `themeRoot.DataContext = BigModeViewModel`

For navigation, use the shared cursor described in Section 13:

- `NavigationEntries`, `SelectedEntry`, `SelectedEntryIndex`: visible direct nodes
  and media in the current level, with a single shared selection.
- `CurrentNode` / `ThemeContextNode`: opened location and theme owner.
- `SelectedCategory` / `SelectedItem`: mutually exclusive preview selections.
- `IsGameListActive` / `IsCategorySelectionActive`: media/node presentation flags.
  An empty level has no selected entry; use `IsNavigationEmpty` when needed.
- `SelectedEntry.Title`, `.Description`, `.CoverPath`, `.LogoPath`: common mixed-entry
  presentation; media-specific fields such as `SelectedYear`, `SelectedDeveloper`
  and `SelectedItem.IsFavorite` remain available for media selections.

For artwork:

- `ActiveWallpaperPath` uses the central asset resolver: item wallpaper, item
  screenshot, then enabled owner-node wallpaper fallback. It is a media-preview
  property; it does not expose a selected category's wallpaper in the library.
- `ActiveScreenshotFallbackPath` exposes the selected item's screenshot only
  when `ActiveWallpaperPath` is empty. Use it for a fallback layer without
  decoding a hidden screenshot underneath an available wallpaper.
- `SelectedCategory.PrimaryWallpaperAbsolutePath` and `.PrimaryLogoAbsolutePath`
  expose a selected node's own artwork. `ActiveCategoryWallpaperPath` and
  `ActiveCategoryLogoPath` additionally require that node's corresponding fallback
  switch in the normal library view; choose the direct properties for node previews.
- `ActiveLogoPath`, `ActiveMarqueePath`, `ActiveControlPanelPath` use the existing
  media/owner resolver; `ActiveBezelPath` is updated with a short debounce.

Library assets resolve under the portable data root. Keep their resolved bindings
separate from theme-local relative paths. For localized static labels, use
`Retromind.Resources.ThemeStrings` or existing `Strings` properties; input hints
should come from the host/VM so they reflect configured bindings.

> Note: Some existing themes in Retromind use helpers (e.g. async image loading). That’s optional; you can use standard Avalonia controls if you prefer.

---

## 4) Video preview: Main & Secondary

Retromind renders videos via LibVLC into one or more `IVideoSurface` instances.  
Themes never talk to LibVLC directly — you only decide **where** and **how** the video appears.

There are two logical channels:

- **Main channel** – per-item/system preview (host‑managed, crossfaded)
- **Secondary channel** – optional background / B‑Roll (theme-configured, host/VM-managed)

The `BigModeViewModel` exposes:

- `bool MainVideoHasContent`
- `bool MainVideoIsPlaying`
- `bool IsVideoOverlayVisible`: visibility used by the host's shared main control.
- `MainVideoSurfaceA`, `MainVideoSurfaceB`, `MainVideoActiveSurfaceIndex`,
  `MainVideoFrameRevision`: existing dual-surface presentation bindings.

- `IVideoSurface? SecondaryVideoSurface`
- `bool SecondaryVideoHasContent`
- `bool SecondaryVideoIsPlaying`

### 4.1 Main video (per-item preview, host‑managed)

The host injects a **shared, crossfading** video control into a **named slot** in your theme.  
Provide a container with a matching name: a `Border`/other `Decorator`, a
`ContentControl`, or a `Panel`. A plain control cannot host the shared surface.
The host replaces the slot's child/content, or clears a panel's children before
adding its shared control. Place fallback visuals in a separate sibling layer.

Default slot name: `VideoSlot` (can be overridden via `ThemeProperties.VideoSlotName`).

```xml
<Grid MinWidth="800" MinHeight="450">
    <!-- A fallback survives because it is outside the slot. -->
    <TextBlock Text="{x:Static resources:ThemeStrings.NoPreviewVideo}"
               IsVisible="{Binding !IsVideoOverlayVisible}"
               HorizontalAlignment="Center" VerticalAlignment="Center" />
    <Border x:Name="VideoSlot" CornerRadius="6" ClipToBounds="True" />
</Grid>
```

Behavior:
- The shared pipeline sequences the two preview surfaces and video/audio fades.
  By default it clears previous frames and fades in the new first frame; retaining
  the old surface requires `VideoRetainPreviousFrameDuringFade="True"`.
- If the **same video** is selected again, it keeps playing (no restart).
- If no video is available, the shared control is hidden. A transparent slot leaves
  sibling artwork/fallbacks visible; its own opaque background would still paint.

**Crossfade timing**
- Controlled by `ThemeProperties.VideoFadeDurationMs` on the theme root (default: `250` ms).

#### Aspect-fitted preview slots (optional)

`ext:VideoPreviewWidthConverter` is a reusable theme extension in
`clr-namespace:Retromind.Extensions;assembly=Retromind`. It sizes a floating main-video
slot from the active frame's aspect ratio. A right-aligned slot then keeps both 4:3
and 16:9 footage at the same right edge, without the extra side space of a wider slot.
Use `ThemeProperties.VideoStretchMode="Uniform"` to preserve the complete video.

Configure `PreviewHeight` (default `535`) to match the slot height and `MaxWidth`
(default `950`) to bound its width. Unknown/inactive frame sizes use `MaxWidth`.
The four ordered bindings are the active index, surfaces A/B, and first-frame
revision. The revision makes dimensions available after the first frame without
polling. The extension reads existing surface dimensions and creates no player.

```xml
<UserControl.Resources>
    <ext:VideoPreviewWidthConverter x:Key="PreviewWidth"
                                   PreviewHeight="450" MaxWidth="800" />
</UserControl.Resources>

<Border x:Name="VideoSlot" Height="450"
        HorizontalAlignment="Right" ClipToBounds="True">
    <Border.Width>
        <MultiBinding Converter="{StaticResource PreviewWidth}">
            <Binding Path="MainVideoActiveSurfaceIndex" />
            <Binding Path="MainVideoSurfaceA" />
            <Binding Path="MainVideoSurfaceB" />
            <Binding Path="MainVideoFrameRevision" />
        </MultiBinding>
    </Border.Width>
</Border>
```

### 4.2 Secondary video (background / B‑Roll)

The secondary channel is intended for theme‑level videos, for example:

- a moving background (wallpaper),
- a small system intro loop,
- decorative B‑Roll.

The pipeline is:

1. The theme root specifies a **theme‑local relative path** via `ThemeProperties.SecondaryBackgroundVideoPath`, e.g.:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             Background="Black"
                
             ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/NameOfVideo.mp4">
</UserControl>
```

This path is relative to the theme folder (e.g. `Themes/Arcade/Videos/bkg_anim.mp4`).

2. `ThemeLoader` reads that property and stores it in `Theme.SecondaryBackgroundVideoPath`.

3. `BigModeViewModel` resolves the path through `ThemePathResolver` within the theme
   directory, loads the secondary channel and exposes:

    - `SecondaryVideoSurface`
    - `SecondaryVideoHasContent`
    - `SecondaryVideoIsPlaying`

Prefer declaring `SecondaryVideoSlotName` and providing an empty named container;
the host attaches its shared secondary control there. Existing themes may also
bind `VideoSurfaceControl` to `SecondaryVideoSurface`; that control only renders
the existing channel and creates no player. Choose one presentation for the channel.
The host's secondary control uses `Uniform`. A direct `VideoSurfaceControl` binding
can choose another `Stretch` value when needed.

Example: full-screen secondary slot with a static theme-local fallback:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:helpers="clr-namespace:Retromind.Helpers;assembly=Retromind"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel" 
             Background="Black" 
             
             ext:ThemeProperties.Name="VideoBackdrop"
             ext:ThemeProperties.SecondaryVideoSlotName="BackgroundVideoSlot"
             ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4">
    
    <UserControl.Resources>
        <helpers:ThemeAssetToBitmapConverter x:Key="ThemeAssetConverter" />
        <helpers:BoolToOpacityConverter x:Key="BoolToOpacity" TrueOpacity="1.0" FalseOpacity="0.0" />
        <helpers:BoolToOpacityConverter x:Key="BoolToOpacityInverse" TrueOpacity="0.0" FalseOpacity="1.0" />
    </UserControl.Resources>
    
    <Grid>
        <!-- Background: video wallpaper + fallback image + dark tint -->
        <Grid>
            <!-- Video background (secondary channel) -->
            <Border x:Name="BackgroundVideoSlot"
                    IsVisible="{Binding SecondaryVideoHasContent}" />
    
            <!-- Static fallback wallpaper when no background video is available -->
            <Image Source="{Binding $parent[UserControl],
                                    Converter={StaticResource ThemeAssetConverter},
                                    ConverterParameter=Images/background.jpg}"
                   Stretch="UniformToFill"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   IsHitTestVisible="False"
                   Opacity="{Binding SecondaryVideoHasContent,
                                     Converter={StaticResource BoolToOpacityInverse}}"/>
    
            <!-- Dark tint above image/video to keep text readable -->
            <Rectangle Fill="#AA101015" IsHitTestVisible="False" />
        </Grid>
    
        <!-- Your foreground layout (cabinet, logo list, info bar, etc.) -->
    </Grid>
</UserControl>
```

Behavior:

- If the configured video file exists and the channel is enabled, the shared
  secondary surface is shown and the fallback image is hidden.
- If it does not exist (or video is disabled) → no secondary video, only the static wallpaper is visible.

Note:

- `ThemeAssetToBitmapConverter` now resolves assets relative to the *theme view instance*.
  Bind the converter to the root view (e.g. `$parent[UserControl]` or an explicit root name)
  and pass the relative asset path via `ConverterParameter`.

---

## 4.3) Crossfade images (optional)

For smooth image transitions (e.g. wallpapers, covers, logos), use the `ext:CrossfadeImage`
control. It loads images asynchronously and crossfades when the bound `Url` changes.

Example:
```xml
<ext:CrossfadeImage Url="{Binding ActiveWallpaperPath}"
                    DecodeWidth="1920"
                    Stretch="UniformToFill" />
```

Supported properties:
- `Url` (string?, file path or web URL)
- `DecodeWidth` (int?, optional)
- `DisableCache` (bool, default `false`; shared image caching is normally preferred)
- `Stretch` (Avalonia Stretch)
- `FadeDurationMs` (int, optional; when 0, uses `ThemeProperties.FadeDurationMs`)
- `FadeDelayMs` (int, optional; delay before switching to the new image)
- `LoadDelayMs` (int, optional; waits for a rapidly changing URL to settle before decoding)
- `RetainCurrentImageUntilLoaded` (bool, default `true`; set to `false` when stale artwork must disappear immediately after a URL change)
- `FadeOutOnClear` (bool, default `false`; optionally fade out a cleared URL)
- `IsCurrentImageReady` (read-only bool; true for a loaded current image or an empty URL)

Tip:
- For a visible crossfade, keep the control present in the visual tree.
  Toggling `IsVisible` to `false` will hide it immediately.

`helpers:ObjectConverters.WhenTrue` is a general `MultiBinding` converter: the first binding is a boolean
condition, the second is the value to return when true; false or unset conditions return `null`.
For example, gate `CrossfadeImage.Url` with an artwork layer's visibility. Setting the URL to null cancels
pending loading and clears retained control buffers (with the default `FadeOutOnClear="False"`), whereas
hiding the control alone keeps its images. Shared cached bitmaps remain available for later reuse.

---

## 5) ThemeProperties: “manifest” + tuning API

`ThemeProperties` are **attached properties** meant to be set on the theme root.  
They act as a theme “manifest” (metadata/sounds) and as a tuning API (UX/animation/typography/layout).

Example:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             Background="Black"

             ext:ThemeProperties.Name="Wheel"
             ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
             ext:ThemeProperties.Version="1.0.0"
             ext:ThemeProperties.WebsiteUrl="https://example.invalid"

             ext:ThemeProperties.AccentColor="#FFD700"
             ext:ThemeProperties.PrimaryVideoEnabled="True"
             ext:ThemeProperties.VideoSlotName="VideoSlot"

             ext:ThemeProperties.SelectedScale="1.10"
             ext:ThemeProperties.UnselectedOpacity="0.60"
             ext:ThemeProperties.SelectedGlowOpacity="0.55"
             ext:ThemeProperties.SelectedGlowRadius="22"

             ext:ThemeProperties.FadeDurationMs="250"
             ext:ThemeProperties.MoveDurationMs="250"

             ext:ThemeProperties.PanelPadding="30"
             ext:ThemeProperties.HeaderSpacing="12"

             ext:ThemeProperties.TitleFontSize="48"
             ext:ThemeProperties.BodyFontSize="18"
             ext:ThemeProperties.CaptionFontSize="14">
    <!-- Theme layout -->
</UserControl>
```

---

## 6) ThemeProperties reference

### 6.1 Metadata (optional)

- `ThemeProperties.Name` (string?)
- `ThemeProperties.Author` (string?)
- `ThemeProperties.Version` (string?)
- `ThemeProperties.WebsiteUrl` (string?)

These are read into the loaded `Theme` for metadata and crediting.
`ThemeProperties.ThemeBasePath` is assigned per view by the loader; themes should
leave it managed and use the scoped asset/font helpers for relative theme files.

### 6.2 Sounds (optional)

- `ThemeProperties.NavigateSound` (string?)
- `ThemeProperties.ConfirmSound` (string?)
- `ThemeProperties.CancelSound` (string?)

Example:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             ext:ThemeProperties.NavigateSound="sounds/navigate.wav"
             ext:ThemeProperties.ConfirmSound="sounds/confirm.wav"
             ext:ThemeProperties.CancelSound="sounds/cancel.wav">
    <!-- Theme layout -->
</UserControl>
```

> Tip: Use paths relative to your theme folder.

### 6.3 Video

- `ThemeProperties.PrimaryVideoEnabled` (bool?, property default: `null`)
  `ThemeLoader` treats an omitted value as `true` for the main preview channel.
- `ThemeProperties.SecondaryVideoEnabled` (bool?, default: `null`)  
  Enables/disables the **secondary** background channel.  
  When not set, the host enables it only if `SecondaryBackgroundVideoPath` is set.
- `ThemeProperties.VideoSlotName` (string, default: `"VideoSlot"`)  
  Slot name for the main preview channel (host injects the crossfading control here).
- `ThemeProperties.SecondaryVideoSlotName` (string?, optional)  
  Slot name for the secondary channel if you want to place it explicitly in the layout.
- `ThemeProperties.VideoStretchMode` (string, declared default: `"Fill"`)
  Supports `Fill`, `Uniform`, `UniformToFill`. Set it explicitly on the root theme;
  the host's shared main control starts with `Uniform` and only applies an explicitly
  declared override. System-subtheme swaps currently retain the outer host's stretch choice.
  Omitting the property retains the shared control's current stretch across root swaps.
- `ThemeProperties.VideoCornerRadius` (CornerRadius, default: `12`) and
  `VideoBorderThickness` (Thickness, default: `0`) are declared authoring values.
  The host does not automatically apply them to the slot; set/bind the actual
  slot's `CornerRadius` and `BorderThickness` in your AXAML.
- `ThemeProperties.VideoFadeDurationMs` (int, default: `250`)  
  Crossfade duration for the **main** preview channel (video + audio).
- `ThemeProperties.VideoRetainPreviousFrameDuringFade` (bool, default: `false`)
  When `false`, clears the old video surface immediately and fades in only the
  new first frame. Set it to `true` only when the theme intentionally crossfades
  from the previous selection's video.
- `ThemeProperties.SecondaryBackgroundVideoPath` (string?, optional)  
  Theme-local relative path to a background video for the secondary channel.  
  Example: `Videos/NameOfVideo.mp4`, validated inside the theme directory through
  `ThemePathResolver`.

Example:
```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             ext:ThemeProperties.PrimaryVideoEnabled="True"
             ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4"
             ext:ThemeProperties.VideoFadeDurationMs="250">
             <!-- layout -->
</UserControl>
```

### 6.4 Optional BigMode Home screen

Root themes can opt into Retromind's non-persisted dashboard with:

```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             ext:ThemeProperties.SupportsHome="True">
    <!-- The regular library layout -->
</UserControl>
```

- `ThemeProperties.SupportsHome` is `false` by default. Existing themes therefore keep
  their classic root navigation without any changes.
- `AppSettings.EnableBigModeHomeScreen` is the user-facing master switch. Home is available
  only when both the setting and the root theme capability are enabled.
- The opt-in belongs on the configured virtual-root theme. All themes shipped with Retromind
  enable it; system subthemes do not need the property.
- The Home view, its primary-video slot, layout, and interaction are owned by the BigMode host.
  Themes must not duplicate the Home markup and continue to define only their regular library
  presentation through `ThemeProperties.VideoSlotName` and the existing theme API.

Home data is derived from the real library at BigMode startup and is never serialized as
nodes or items. Retromind keeps the original source node with every shortcut so inherited
artwork, video, launch settings, and parental filtering remain intact.

The Library row is a shallow navigator over the actual node tree. With one root it starts
at that root; with multiple roots it starts at a virtual Library overview. Activating any child
opens its level in the Home library row, including leaves and mixed nodes. Direct media appear
after the child nodes and activate through the existing launcher. The left current-node card opens that node directly, and a Back card appears at the end
of the row below the initial level (also reachable by moving left from the current card).
Library cards activate with the configured Select action (default A/Cross or Enter)
or a primary mouse click. This navigation state is UI-only
and does not alter or persist the real tree.

The host owns input dispatch. The configured Home action (default Y/Triangle or
keyboard Home) toggles Home against the previous library bookmark. The configured
Back action (default B/Circle or Backspace) exits BigMode while Home is active;
the separate Exit action defaults to Escape on the keyboard.
Direct `--bigmode` startup opens Home; launching BigMode from the desktop keeps an explicit
item selection and otherwise opens Home.

### 6.5 Tuning: selection/focus (controller-friendly)

These values are used by the host to produce consistent “TV-like” selection behavior.

- `ThemeProperties.SelectedScale` (double, default: `1.06`)
- `ThemeProperties.UnselectedOpacity` (double, default: `0.75`)
- `ThemeProperties.SelectedGlowOpacity` (double, default: `0.35`)
- `ThemeProperties.SelectedGlowRadius` (double, default: `18.0`) — blur radius for host-driven glow
- `ThemeProperties.AccentColor` (Color?) — used for the glow color when set (fallback: gold)
- `ThemeProperties.CircularWindowSize` (int, default: `9`)
- `ThemeProperties.CircularLogoPreloadCount` (int, default: `0`) — cache a bounded
  number of logos immediately beyond each circular-window edge when opted in.
- `ThemeProperties.UseHostSelectionEffects` (bool, default: `true`) — host paints deterministic selected/unselected rows
- `ThemeProperties.UseHostListGuardrails` (bool, default: `true`) — host applies compositor-safe ListBox defaults (focus/hit-test/layout/background)

Recommended ranges:

- `SelectedScale`: 1.05–1.15
- `UnselectedOpacity`: 0.45–0.80
- `SelectedGlowOpacity`: 0.20–0.70
- `SelectedGlowRadius`: 10–36 (higher values are softer/broader but more expensive)

Note:
- Even when `ThemeProperties.UseHostSelectionEffects="False"` is set on a `ListBox`,
  you can still bind `SelectedGlowOpacity` / `SelectedGlowRadius` directly in your
  theme XAML (for custom per-item glow implementations).
- `UseHostListGuardrails` is recommended for all navigation lists; disable it only
  when your theme intentionally needs mouse-driven ListBox interaction.

Guardrails set `IsHitTestVisible=False`, `Focusable=False`, `ClipToBounds=True` and
`UseLayoutRounding=True`, and can replace a transparent viewport background with a
compositor-safe fill. Disabling host selection effects does not disable guardrails.

#### Circular list windows (wrap-around)

Some themes use a "circular window" list that always keeps the selected item in
the middle and wraps around at the ends (so the last items appear above the first
item).

How to enable it:

1) Set the window size on the theme root:
```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             ext:ThemeProperties.CircularWindowSize="9">
</UserControl>
```

2) For a specialized media-only scene, bind to `CircularItems`:
```xml
<ListBox ItemsSource="{Binding CircularItems}"
         SelectedItem="{Binding SelectedItem, Mode=OneWay}" />
```

Notes:
- `CircularWindowSize <= 0` shows the full list (no windowing).
- Keep the size odd so the selected item can be centered.
- Keep this window's selection binding one-way. Circular/repeating windows are
  demand-driven and retain overlapping entries instead of rebuilding the whole list.
- Themes that need native mouse selection should instead bind to the stable
  `NavigationEntries` collection with `SelectedEntry` and a `VirtualizingStackPanel`.
- `CircularItems` contains media only. Mixed wheel themes use `WheelEntries` and
  `WheelSelectedEntryIndex`; neither window is a replacement navigation stack.

### 6.6 Dynamic artwork accents

Dynamic accents are opt-in. When enabled, the BigMode host extracts a vivid
primary and secondary color from the active artwork and updates the colors on
the theme root. Existing themes remain unchanged.

- `ThemeProperties.DynamicAccentEnabled` (bool, default: `false`)
  Enables artwork analysis for this theme.
- `ThemeProperties.DynamicAccentTransitionMs` (int, default: `400`)
  Duration of the transition from the current palette to the new palette.
  Values are clamped to `0–5000` ms.
- `ThemeProperties.DynamicAccentColor` (Color, default: `#62E6FF`)
  Initial/fallback primary color and host-updated output color.
- `ThemeProperties.DynamicSecondaryAccentColor` (Color, default: `#9B7BFF`)
  Initial/fallback secondary color and host-updated output color.

The artwork source is exposed to themes as `DynamicAccentArtworkPath`. For
games, Retromind resolves item wallpaper → screenshot → cover → inherited
wallpaper. For categories it resolves wallpaper → cover. Only local existing
artwork is analyzed; missing, neutral, or unsuitable artwork retains the
configured fallback colors.

Analysis starts after a short navigation debounce and palettes are cached by
file path, size, and modification time. This prevents held-direction navigation
and Attract Mode from decoding every intermediate image.

Example:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             xmlns:helpers="clr-namespace:Retromind.Helpers;assembly=Retromind"
             x:Name="ThemeRoot"
             ext:ThemeProperties.AccentColor="#62E6FF"
             ext:ThemeProperties.DynamicAccentEnabled="True"
             ext:ThemeProperties.DynamicAccentTransitionMs="400"
             ext:ThemeProperties.DynamicAccentColor="#62E6FF"
             ext:ThemeProperties.DynamicSecondaryAccentColor="#9B7BFF">
    <UserControl.Resources>
        <helpers:ColorToBrushConverter x:Key="AccentBrushConverter" />
        <helpers:ColorToBrushConverter x:Key="FaintAccentBrushConverter"
                                       Opacity="0.10" />
    </UserControl.Resources>

    <Grid>
        <Border Background="{Binding ElementName=ThemeRoot,
                                     Path=(ext:ThemeProperties.DynamicAccentColor),
                                     Converter={StaticResource AccentBrushConverter}}" />

        <Ellipse Fill="{Binding ElementName=ThemeRoot,
                                Path=(ext:ThemeProperties.DynamicSecondaryAccentColor),
                                Converter={StaticResource FaintAccentBrushConverter}}" />
    </Grid>
</UserControl>
```

`ColorToBrushConverter.Opacity` accepts a value from `0` to `1` and is useful
for producing full, soft, and faint brushes from the same dynamic color.
`ThemeProperties.AccentColor` follows the dynamic primary color while the
feature is enabled, preserving compatibility with existing custom glow
bindings. Themes may also bind `GradientStop.Color` directly to either dynamic
color property.

### 6.7 Achievement progress (RetroAchievements, Steam and GOG)

BigMode exposes the shared provider-specific progress view model through
`RetroAchievementsProgress`; `AchievementProgress` is an alias. The historical
name also covers Steam and GOG. Eligible identified media are loaded after a
450 ms stable selection, using the configured integration/display settings.
RetroAchievements uses a persisted game identity; Steam uses its App ID, and GOG
uses the linked store game with Galaxy integration enabled. The central VM/service
chooses the provider; themes should not implement provider selection or API calls.
Changing the selection clears previous progress immediately. Nodes have no progress,
and Attract Mode does not issue progress requests.

Useful summary bindings are:

- `RetroAchievementsProgress.IsVisible`
- `RetroAchievementsProgress.Title` (localized provider-specific progress title)
- `RetroAchievementsProgress.IsLoading`
- `RetroAchievementsProgress.HasProgress`
- `RetroAchievementsProgress.ProgressValue`
- `RetroAchievementsProgress.ProgressMaximum`
- `RetroAchievementsProgress.SummaryText`
- `RetroAchievementsProgress.HardcoreSummaryText`
- `RetroAchievementsProgress.ProviderDetailText` (same value as `HardcoreSummaryText`;
  Hardcore counts for RetroAchievements, remaining hidden counts for Steam/GOG)
- `RetroAchievementsProgress.ShowStatus`
- `RetroAchievementsProgress.StatusText`

Hide the complete container through `IsVisible`. It requires an eligible provider
and a loaded positive achievement count; initial loads, empty sets and failures
without a known set stay hidden. A refresh can retain a known set while displaying
`IsLoading` / `ShowStatus` (including error or cached-fallback status). Use
`HasProgress` for controls requiring a snapshot. The host refreshes
the selected game's progress after a normally tracked play session. Failed
starts, untracked launcher handoffs, and test launches do not trigger a refresh.

The full achievement browser is owned by `BigModeHostView`, not by the active
theme. When `CanOpenAchievementsOverlay` is true, the host displays the
shared hint derived from the configured Details action (default X/Square or I).
Themes should therefore provide only an optional compact summary and
must not duplicate the detailed badge grid. While the browser is open, the host
routes directional/page input to the badge grid; the configured Details, Back and
Exit actions close it. Preserve this host-owned behavior when adding a summary.

Example:

```xml
<StackPanel IsVisible="{Binding RetroAchievementsProgress.IsVisible}">
    <TextBlock Text="{Binding RetroAchievementsProgress.Title}" />
    <ProgressBar Minimum="0"
                 Maximum="{Binding RetroAchievementsProgress.ProgressMaximum}"
                 Value="{Binding RetroAchievementsProgress.ProgressValue}"
                 IsVisible="{Binding RetroAchievementsProgress.HasProgress}" />
    <TextBlock Text="{Binding RetroAchievementsProgress.SummaryText}" />
    <TextBlock Text="{Binding RetroAchievementsProgress.HardcoreSummaryText}" />
    <ProgressBar IsIndeterminate="{Binding RetroAchievementsProgress.IsLoading}"
                 IsVisible="{Binding RetroAchievementsProgress.IsLoading}" />
    <TextBlock Text="{Binding RetroAchievementsProgress.StatusText}"
               IsVisible="{Binding RetroAchievementsProgress.ShowStatus}" />
</StackPanel>
```

Achievement badge paths remain lazy. A compact theme summary does not download
badges merely because the user scrolls over a game; the host-owned detailed
achievement browser requests them only when it is opened. Its relevant host
state is exposed as `CanOpenAchievementsOverlay`, `IsAchievementsOverlayOpen`,
and `SelectedAchievement`, but themes normally do not need to bind these
properties themselves.

### 6.8 Tuning: animations

- `ThemeProperties.FadeDurationMs` (int, default: `200`)
- `ThemeProperties.MoveDurationMs` (int, default: `160`)

“Snappy” UI: ~120–180ms  
“Cinematic” UI: ~220–320ms

### 6.9 Tuning: layout

- `ThemeProperties.PanelPadding` (Thickness, default: `20`)
- `ThemeProperties.HeaderSpacing` (double, default: `10`)
- `ThemeProperties.TileSize` (double, default: `220`)
- `ThemeProperties.TileSpacing` (double, default: `12`)
- `ThemeProperties.TileCornerRadius` (CornerRadius, default: `12`)
- `ThemeProperties.TileBorderThickness` (Thickness, default: `0`)

Additional authoring values are `BackgroundDimOpacity` (double, default `0.35`)
and `PanelBackgroundOpacity` (double, default `0.18`). Tile sizing, corner/border,
background/panel opacity values do not automatically reshape or recolor controls.
Bind them explicitly in the theme. `PanelPadding` and `HeaderSpacing` are applied
by the host only to their opt-in classes (Section 7).

### 6.10 Tuning: typography (TV-friendly defaults)

- `ThemeProperties.TitleFontSize` (double, default: `34`)
- `ThemeProperties.BodyFontSize` (double, default: `18`)
- `ThemeProperties.CaptionFontSize` (double, default: `14`)

Optional font families (system name or theme-relative file path):

- `ThemeProperties.TitleFontFamily` (string?, optional)
- `ThemeProperties.BodyFontFamily` (string?, optional)
- `ThemeProperties.CaptionFontFamily` (string?, optional)
- `ThemeProperties.MonoFontFamily` (string?, optional)

Font family notes:
- Use system font names like `IBM Plex Sans`, or theme-relative file paths like `Fonts/IBMPlexSans-Regular.ttf`.
- Supported file extensions: `.ttf`, `.otf`, `.ttc`.
- Fonts apply to `TextBlock` elements using the matching classes (see Section 7.2).

Example:
```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             ext:ThemeProperties.TitleFontFamily="Fonts/Fraunces-SemiBold.ttf"
             ext:ThemeProperties.BodyFontFamily="Fonts/IBMPlexSans-Regular.ttf"
             ext:ThemeProperties.CaptionFontFamily="Fonts/IBMPlexSans-SemiBold.ttf"
             ext:ThemeProperties.MonoFontFamily="Fonts/IBMPlexMono-Regular.ttf">
    <!-- layout -->
</UserControl>
```

### 6.11 Attract Mode (auto-random selection on idle)

Some BigMode themes (especially arcade-style layouts) may want to automatically
scroll/select random games after a period of user inactivity — similar to an
"attract mode" in classic arcade cabinets.

This behavior is fully opt-in and controlled per theme via `ThemeProperties`:

- `ThemeProperties.AttractModeEnabled` (bool, default: `false`)  
  Enables the attract mode logic for this theme.
- `ThemeProperties.AttractModeIdleSeconds` (int, default: `0`)  
  Idle time in **seconds** before the first random selection.  
  Every additional multiple of this interval (2×, 3×, …) will trigger another
  random selection while the user remains inactive.
- `ThemeProperties.AttractModeSound` (string?, optional)  
  Theme-local path to a short sound effect that is played when the attract-mode
  "spin" animation starts (e.g. `sounds/attract_spin.wav`).

The ViewModel exposes an additional flag:

- `IsInAttractMode` (bool) — `true` while the attract-mode animation is actively
  spinning through the list. Themes can use this to drive temporary visual
  effects (glow, shake, etc.) on the selected item.

Example (Arcade theme):
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind" x:DataType="vm:BigModeViewModel" Background="Black"
         ext:ThemeProperties.Name="Arcade"
         ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
         ext:ThemeProperties.Version="0.1.0"
         ext:ThemeProperties.PrimaryVideoEnabled="True"
         ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4"
         ext:ThemeProperties.AttractModeEnabled="True"
         ext:ThemeProperties.AttractModeIdleSeconds="60"
         ext:ThemeProperties.AttractModeSound="sounds/attract_spin.wav">
</UserControl>
```

Details:

- Attract Mode operates in library frames containing visible direct media, even if
  the current selection is a node. It pauses on Home, while launching, and during
  system-menu/achievement overlays. Nodes are never opened and media are never launched.
- Selections are performed using Retromind’s `RandomHelper`; the ViewModel
  simply moves the selection to a random item in the current list.
- Any user input (navigation, select, back) resets the idle timer and the
  internal step counter.

In this example:

- After 60 seconds of no input in game view, one random game is selected.
- After 120 seconds, another random game is selected.
- After 180 seconds, a third one, and so on — until the user interacts again.

---

### 6.12 Host selection effects for lists (zoom/opacity/glow)

By default, the BigMode host applies generic selection effects to all `ListBox`
instances in the theme:

- Zoom of the selected item (`SelectedScale`)
- Dimmed, unselected items (`UnselectedOpacity`)
- A subtle glow around the selected item (`SelectedGlowOpacity`)
- Glow size controlled by `SelectedGlowRadius`
- Animated transitions (opacity / transform)

These effects are convenient for "standard" lists, but they can conflict with
very specialized themes that render their **own** selection visuals
(e.g. custom logo rails or bespoke layout transforms).

Via the attached property

- `ThemeProperties.UseHostSelectionEffects` (bool, default: `true`)

a theme can control **per ListBox** whether the host is allowed to apply its
standard selection effects.

#### 6.12.1 Default behavior (host effects enabled)

If you don't set anything, the behavior is:
```xml
<UserControl>
    <ListBox ItemsSource="{Binding NavigationEntries}" SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"></ListBox>
</UserControl>
```

The host reads the global tuning values from the theme root:
```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             
             ext:ThemeProperties.SelectedScale="1.08"
             ext:ThemeProperties.UnselectedOpacity="0.70"
             ext:ThemeProperties.SelectedGlowOpacity="0.40"
             ext:ThemeProperties.SelectedGlowRadius="20"
             ext:ThemeProperties.AccentColor="#00D1FF">
</UserControl>
```

and automatically applies them to all `ListBoxItem`s.

#### 6.12.2 Explicitly disabling host effects (custom zoom logic)

If a particular `ListBox` implements its **own** selection animation
(e.g. via `LayoutTransformControl` + converter bound to `IsSelected`), you can
turn off the host effects explicitly:

```xml
<UserControl>
    <ListBox xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             
             ext:ThemeProperties.UseHostSelectionEffects="False"
             ItemsSource="{Binding NavigationEntries}"
             SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"></ListBox>
</UserControl>
```

In this case:

- the host does **not** register selection handlers for this ListBox,
- it does not change `Opacity`, `RenderTransform`, or `Effect` on the items,
- all selection visuals are fully controlled by the theme.

#### 6.12.3 Example: custom mixed-entry visuals

This example dims unselected titles through the theme's own converter. The list
keeps the canonical mixed selection and opts out of host selection effects.
List guardrails remain enabled.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             xmlns:helpers="clr-namespace:Retromind.Helpers;assembly=Retromind"
             xmlns:nav="clr-namespace:Retromind.Services.BigMode;assembly=Retromind"
             ext:ThemeProperties.NavigationPresentation="MixedEntries">
    <UserControl.Resources>
        <helpers:BoolToOpacityConverter x:Key="SelectionOpacity"
                                       TrueOpacity="1" FalseOpacity="0.6" />
    </UserControl.Resources>
    <ListBox ItemsSource="{Binding NavigationEntries}"
             SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
             SelectedIndex="{Binding SelectedEntryIndex, Mode=OneWay}"
             ext:ThemeProperties.UseHostSelectionEffects="False">
        <ListBox.ItemsPanel>
            <ItemsPanelTemplate><VirtualizingStackPanel /></ItemsPanelTemplate>
        </ListBox.ItemsPanel>
        <ListBox.ItemTemplate>
            <DataTemplate x:DataType="nav:BigModeNavigationEntry">
                <TextBlock Text="{Binding Title}"
                           Opacity="{Binding $parent[ListBoxItem].IsSelected, Converter={StaticResource SelectionOpacity}}" />
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>
</UserControl>
```

### 6.13 System menu and configurable input hints

The host owns the system menu and controller/keyboard dispatch. Use the existing
`SystemMenuHintText`, `HomeHintText` and `AchievementsOverlayHintText` strings when
showing shortcuts, so remapped buttons/keys are reflected automatically. Theme
commands such as `PlayCurrentCommand` remain semantic actions; do not hardcode a
controller assignment or create a parallel input handler.

`SystemMenuHintText` uses one compact controller name and the configured keyboard key, followed by the
localized action, e.g. `Start / F10 → System menu`. It reads both assignments from the current settings;
the controller-layout overview retains its full platform aliases.

`ThemeProperties.ProvidesSystemMenuHint` (bool, default `false`) tells the host that
the root theme renders its own menu hint. When true, supply a hint bound to
`SystemMenuHintText` and hide it while `IsSystemMenuOpen`. The host retains its own
hint on Home. When false, the host provides the generic menu hint for the library.

---

## 7) Classes: opt-in hooks for automatic styling

To keep theme authoring simple, the host supports a few **opt-in classes**.

### 7.1 Panels

#### `rm-panel`
Apply consistent padding/margins for content panels.

- If the control is a `Border`: `Padding = ThemeProperties.PanelPadding`
- Otherwise: `Margin = ThemeProperties.PanelPadding`

#### `rm-header`
Header spacing for stacked header sections.

- If the control is a `StackPanel`: `Spacing = ThemeProperties.HeaderSpacing`


Example:
```xml
<Border Classes="rm-panel">
  <StackPanel Classes="rm-header">
    <TextBlock Text="Title"/>
    <TextBlock Text="Subtitle"/>
  </StackPanel>
</Border>
```

### 7.2 Typography

#### `rm-title`, `rm-body`, `rm-caption`, `rm-mono`
Apply default font sizes and optional font families to `TextBlock` elements:

- `rm-title` → `TitleFontSize`
- `rm-body` → `BodyFontSize`
- `rm-caption` → `CaptionFontSize`
- `rm-mono` → `MonoFontFamily` (family only)

Example:
```xml
<TextBlock Classes="rm-title"
           Text="{Binding SelectedEntry.Title}"
           FontWeight="Bold"/>

<TextBlock Classes="rm-body"
           Text="{Binding SelectedEntry.Description}"
           TextWrapping="Wrap"/>

<TextBlock Classes="rm-caption"
           Text="{Binding SystemMenuHintText}"/>

<TextBlock Classes="rm-mono"
           Text="ROM: MegaManX.smc"/>
```

---

## 8) Best practices

### 8.1 Provide fallbacks for missing assets
Not all items have:

- cover
- wallpaper
- logo
- video

Prefer “safe” layouts that still look okay with missing images (e.g., show a title text if a logo is missing).

### 8.2 Performance tips
- Keep mixed lists virtualized with `VirtualizingStackPanel` and a bounded viewport.
  A horizontal `StackPanel` around the entire list can defeat virtualization by
  measuring it with unbounded width. A finite reference grid inside a `Viewbox` is
  a suitable scaling option; verify the list still gets finite bounds.
- Use `helpers:AsyncImageHelper.Url` / `DecodeWidth` for covers and `CrossfadeImage`
  for large transitioning artwork. Both use the existing cancellation/cache lane.
  The host enables serial BigMode decoding; image caching is shared and bounded
  by 200 entries and 128 MiB of decoded pixels, not total process memory.
- Leave caching enabled unless a concrete ownership/use case needs uncached images.
  Match decode width to display needs; `LoadDelayMs` avoids loading rapidly skipped
  hero images. `ActiveScreenshotFallbackPath` prevents duplicate full-size backdrops.
- Stable canonical selection retains the entry list. Use demand-driven circular/
  wheel windows only when needed, and avoid rebuilding all cards on selection.
- Avoid extremely heavy effects on every item (big blur radius everywhere can be expensive).
- Let the host handle selection UX when possible (don’t duplicate work in your theme).

### 8.3 Video slot must not be 0×0
Give the slot positive arranged bounds through explicit/minimum dimensions or a
bounded stretching grid cell. The host attaches the managed surface to this slot;
zero-sized bounds produce no visible image even when the central player has audio.

### 8.4 Relative gradients and optional overlays

Use percentage endpoints for gradients spanning a control, for example
`StartPoint="0%,0%" EndPoint="0%,100%"`. Without `%`, `0,1` means a one-pixel endpoint.
A gradient with an opaque final color can therefore hide the entire media layer.
Keep the intended order explicit: artwork, video, local reading shades and UI; a
floating preview can sit above the global shade when it should remain bright.

For optional achievement/status displays, use an independent overlay or bounded
layout area when their visibility must not move other text. Hide the whole summary
through the shared `IsVisible` contract. Keep overlays clear of descriptions, lists
and video, and verify the layout with both present and absent data.

---

## 9) Troubleshooting

### Video does not show up
Checklist:
1. `ext:ThemeProperties.PrimaryVideoEnabled="True"` (or omitted → defaults to true)
2. Slot exists:
    - default: `x:Name="VideoSlot"`
    - or custom `VideoSlotName` + matching `x:Name`
3. Slot has a valid size (width/height > 0)
4. The slot's background and later layers do not cover the shared image. Check
   parent visibility, clipping, opacity, Z order and relative gradient coordinates.
5. Artwork/text fallbacks are siblings, since the host replaces slot content.

### Theme fails with “Unable to resolve type”

Check the namespace/assembly qualification and whether the installed application
contains that extension class. New/moved C# extensions require a new application
build; external AXAML alone does not add .NET types. Also check whether portable
theme synchronization preserved a locally modified copy with older bindings.

### Selection visuals look odd
If you apply very strong custom styles to `ListBoxItem`, it may conflict with host tuning.  
Either rely on host selection defaults, or intentionally tune your styles to match.

---

## 10) Example theme: tuning + hooks

A complete root example with one mixed virtualized list and a transparent main-video
slot. Declare all Retromind namespaces against the application assembly for runtime
loading. This layout uses only the shared VM, host and existing theme API.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:nav="clr-namespace:Retromind.Services.BigMode;assembly=Retromind"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             Background="Black"
             ext:ThemeProperties.Name="MinimalTV"
             ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
             ext:ThemeProperties.Version="1.0.0"
             ext:ThemeProperties.NavigationPresentation="MixedEntries"
             ext:ThemeProperties.SupportsHome="True"
             ext:ThemeProperties.VideoStretchMode="Uniform"
             ext:ThemeProperties.AccentColor="#00D1FF"
             ext:ThemeProperties.SelectedScale="1.08"
             ext:ThemeProperties.UnselectedOpacity="0.65"
             ext:ThemeProperties.PanelPadding="28"
             ext:ThemeProperties.TitleFontSize="44"
             ext:ThemeProperties.BodyFontSize="20">
    <Grid ColumnDefinitions="420,*" Margin="24">
        <Border Classes="rm-panel">
            <Grid RowDefinitions="Auto,*">
                <TextBlock Classes="rm-title" Text="{Binding CategoryTitle}"
                           TextWrapping="Wrap" />
                <ListBox Grid.Row="1" Margin="0,18,0,0"
                         ItemsSource="{Binding NavigationEntries}"
                         SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
                         SelectedIndex="{Binding SelectedEntryIndex, Mode=OneWay}">
                    <ListBox.ItemsPanel>
                        <ItemsPanelTemplate><VirtualizingStackPanel /></ItemsPanelTemplate>
                    </ListBox.ItemsPanel>
                    <ListBox.ItemTemplate>
                        <DataTemplate x:DataType="nav:BigModeNavigationEntry">
                            <TextBlock Text="{Binding Title}" TextWrapping="Wrap" />
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </Grid>
        </Border>
        <Border Grid.Column="1" Classes="rm-panel">
            <Grid RowDefinitions="Auto,*,Auto" RowSpacing="18">
                <TextBlock Classes="rm-title" Text="{Binding SelectedEntry.Title}"
                           TextWrapping="Wrap" />
                <TextBlock Grid.Row="1" Classes="rm-body"
                           Text="{Binding SelectedEntry.Description}"
                           TextWrapping="Wrap" MaxLines="5" TextTrimming="WordEllipsis" />
                <Border Grid.Row="2" x:Name="VideoSlot" Height="450"
                        ClipToBounds="True" />
            </Grid>
        </Border>
    </Grid>
</UserControl>
```

---

## 11) Visual transition slots (Primary / Secondary / Background)

Retromind provides a small, generic animation system for prominent visuals in a theme.
Instead of every theme implementing its own logic, the host can animate up to three
visual "slots" based on `ThemeProperties`:

- **PrimaryVisual** – typically the main cover / hero image for the selected item
- **SecondaryVisual** – e.g. logo, big title area, or a secondary image
- **BackgroundVisual** – a wallpaper or large background container

The host reads these properties from the **theme root** and starts enter animations
when the canonical `SelectedEntry` changes. Slides use a `TranslateTransform` or
an opted-in compositor animation; opacity fades are separate from image crossfades.

### 11.1 Global animation timing

These properties control the default timing for all slots:
```xml
<UserControl ext:ThemeProperties.FadeDurationMs="400"
             ext:ThemeProperties.MoveDurationMs="400">
</UserControl>
    
```

- `FadeDurationMs` – duration for opacity changes
- `MoveDurationMs` – duration for slide/transform changes.
  Use nonnegative milliseconds. Host list effects clamp timing to `0–5000` ms;
  visual-slot enter animations use the configured durations.

### 11.2 PrimaryVisual slot

Properties (set on the root `UserControl`):
```xml
<UserControl ext:ThemeProperties.PrimaryVisualElementName="CoverPanel"
             ext:ThemeProperties.PrimaryVisualEnterMode="SlideFromLeft"
             ext:ThemeProperties.PrimaryVisualEnterOffsetX="-420"
             ext:ThemeProperties.PrimaryVisualEnterOffsetY="0">

    <!-- somewhere in the layout -->
    <Border x:Name="CoverPanel"
            Width="220"
            Height="330"
            Margin="-70,28,0,0">
        <Image/> <!-- some image here -->
    </Border>
</UserControl>
```

Supported `PrimaryVisualEnterMode` values:

- `"None"` – no animation
- `"Fade"` – only opacity 0 → 1
- `"SlideFromLeft"` – translate from the left and fade into place
- `"SlideFromRight"`
- `"SlideFromTop"`
- `"SlideFromBottom"`
- `"ZoomIn"` – scale from `0.8` to `1.0` with a fade
- `"Pulse"` – one pop from `0.9` to `1.05` with a fade

Offsets:

- `PrimaryVisualEnterOffsetX` – horizontal offset in pixels
    - negative = start further left
    - positive = start further right
- `PrimaryVisualEnterOffsetY` – vertical offset in pixels
    - negative = start further up
    - positive = start further down

If offsets are `0`, the host uses reasonable defaults
(e.g. `-420` for `"SlideFromLeft"`).

**Example (Default theme – cover fly-in):**
```xml
<UserControl ext:ThemeProperties.FadeDurationMs="900"
             ext:ThemeProperties.MoveDurationMs="900"
             ext:ThemeProperties.PrimaryVisualElementName="CoverPanel"
             ext:ThemeProperties.PrimaryVisualEnterMode="SlideFromLeft"
             ext:ThemeProperties.PrimaryVisualEnterOffsetX="-420"
             ext:ThemeProperties.PrimaryVisualEnterOffsetY="0">

    <!-- somewhere in the layout -->
    <Border x:Name="CoverPanel"
            Width="220"
            Height="330"
            Margin="-70,28,0,0">
        <Image/> <!-- some image here -->
    </Border>
</UserControl>
```

The host then automatically animates `CoverPanel` when `SelectedEntry` changes.

### 11.3 SecondaryVisual slot

Analogous to `PrimaryVisual`, but intended for a **secondary** element
(e.g. logo, big title text, secondary image).
```xml
<UserControl ext:ThemeProperties.SecondaryVisualElementName="BigTitle"
             ext:ThemeProperties.SecondaryVisualEnterMode="SlideFromTop"
             ext:ThemeProperties.SecondaryVisualEnterOffsetY="-120">
</UserControl>
```

Properties:

- `SecondaryVisualElementName`
- `SecondaryVisualEnterMode`
- `SecondaryVisualEnterOffsetX`
- `SecondaryVisualEnterOffsetY`

Animation modes and semantics are identical to the primary slot.

You are free to decide *which* element is your `SecondaryVisual` – a logo, a title block,
a small preview image, etc.

### 11.4 BackgroundVisual slot

The background slot is useful to softly animate a wallpaper or a large background container.
```xml
<UserControl ext:ThemeProperties.BackgroundVisualElementName="WallpaperImage"
             ext:ThemeProperties.BackgroundVisualEnterMode="Fade">
</UserControl>
```

Properties:

- `BackgroundVisualElementName`
- `BackgroundVisualEnterMode`
- `BackgroundVisualEnterOffsetX`
- `BackgroundVisualEnterOffsetY`

Typical usage is `"Fade"` (an opacity entrance) or a gentle `"SlideFromBottom"` / `"SlideFromTop"`.

### 11.5 How the host uses these slots

Internally, the host:

1. Reads the configured element names and modes from `ThemeProperties`.
2. Finds the corresponding `Control` in the theme by `x:Name`.
3. Applies opacity and transform transitions, or an opted-in compositor slide.
4. On canonical selection changes (`SelectedEntry`), the host sets a start
   opacity/translation/scale and then the target state. Slide animations retain the
   original margin, avoiding a layout pass for every movement frame.

Choose a named wrapper when the artwork inside it needs its own opacity or transform;
the enter animation controls these properties on its target. `Fade` enters the
current visual; use `CrossfadeImage` for an actual crossfade between image contents.

Primary/Secondary/Background all use the same mechanics; only the mapping
from slot → concrete element name is configured via ThemeProperties.

#### Artwork and preview readiness

Set these on the named visual being animated:

- `ThemeProperties.WaitForAsyncImageBeforeEnter` (bool, default `false`) waits for
  a visible, URL-bound `AsyncImageHelper` image within that visual to load. At least
  one eligible image is sufficient. Use it for artwork expected to finish loading;
  this low-level image wait has no timeout of its own.
- `ThemeProperties.WaitForPreviewMediaBeforeEnter` (bool, default `false`) opts the
  **primary visual** into the host's combined artwork/first-video-frame gate.
- `ThemeProperties.UseCompositorEnterAnimation` (bool, default `false`) runs supported
  slide entrances through the compositor when a composition visual is available;
  otherwise the existing transform path is used.

Mark participating `CrossfadeImage` controls with
`ThemeProperties.ContributesToPreviewMediaReadiness="True"` (default `false`). The
primary gate waits for all effectively visible, non-empty participating images and
an applicable reusable/first video frame; it releases after a bounded timeout if
readiness cannot be established. `PreviewMediaReady` (bool, default `true`) is its
host-managed output on the theme root. Leave this value managed by the host.

`VisualBaseMargin` and `VisualBaseMarginInitialized` are internal attached-property
bookkeeping for stable repeated animations, not authoring knobs.

### 11.6 Minimum required setup

For a slot to be animated:

1. The root `UserControl` must define:
    - `ext:ThemeProperties.<Slot>VisualElementName`
    - optionally: `...EnterMode` and `...EnterOffsetX/Y`
2. A `Control` with the matching `x:Name` must exist inside the theme.
3. Global timings (`FadeDurationMs` / `MoveDurationMs`) may be overridden
   but are optional (defaults are used otherwise).
4. The host triggers these animations on canonical `SelectedEntry` changes and
   root/subtheme setup, while suppressing outgoing-view work during theme swaps.

If any of these are missing or a mode is `"None"`, the slot is simply ignored
and no animation is applied.

### 11.7 What visual slots are NOT for

The `Primary/Secondary/BackgroundVisual*` slots are designed for
**single, named controls** (e.g. cover panel, big title block, wallpaper),
which are updated when `SelectedEntry` changes.

They are **not** intended for per-item selection effects inside `ItemsControl`s
(such as zooming a single `ListBoxItem` in a list or carousel):

- Do **use** slots for things like:
    - main cover image
    - hero/title area
    - large background image/container

- Do **not** use slots directly on:
    - individual list items (e.g. each game entry in a wheel/list)
    - templated children inside `ItemsControl`s

For per-item selection visuals (zoom, glow, opacity) in lists, use:

- the host’s selection tuning (`ThemeProperties.SelectedScale`,
  `UnselectedOpacity`, `SelectedGlowOpacity`, `SelectedGlowRadius`) which is applied to
  `ListBoxItem`s by the host, or
- local XAML logic inside the `ItemTemplate`  
  (e.g. `LayoutTransformControl` + `BoolToScaleConverter` bound to `IsSelected`).

## 12) Versioning & compatibility

- ThemeProperties are designed to be optional; missing values fall back to defaults.
- If a theme uses properties unknown to an older Retromind version, it may fail to load.
    - Keep `ThemeProperties.Version` up to date.
    - When publishing a theme, mention the minimum supported Retromind version.
- `ThemeProperties.Version` is theme metadata, not an automatic API compatibility
  check. New C# extension types/namespaces must exist in the installed application.

## 13) System browser themes (platform selection)

Retromind supports a **two-level theming model** for the platform selection in BigMode:

1. A **system host theme** – the main BigMode theme used on the „Systems“ level  
   (e.g. a list of platforms on the left, preview on the right).
2. **System subthemes** – per-platform layouts that are embedded into the host on the right side  
   (e.g. different TV/mockups for C64, SNES, PC, …).

This section describes how to build both.

### Navigation presentation contract

Declare `ext:ThemeProperties.NavigationPresentation` on the root:

- `MixedEntries`: your list/wheel displays nodes and media together.
- `MediaItemsOnly`: your scene presents media; the host adds structural access on mixed/node levels.
- `NodeBrowser`: a structural browser using the same mixed list; System Host additionally supplies previews.
- `LegacySeparate`: default for existing themes without this metadata. The host supplements existing bindings
  so presentation limits cannot hide library contents, even with Home disabled.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:nav="clr-namespace:Retromind.Services.BigMode;assembly=Retromind"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:DataType="vm:BigModeViewModel"
             ext:ThemeProperties.NavigationPresentation="MixedEntries">
    <ListBox ItemsSource="{Binding NavigationEntries}"
             SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
             SelectedIndex="{Binding SelectedEntryIndex, Mode=OneWay}">
        <ListBox.ItemsPanel>
            <ItemsPanelTemplate><VirtualizingStackPanel /></ItemsPanelTemplate>
        </ListBox.ItemsPanel>
        <ListBox.ItemTemplate>
            <DataTemplate x:DataType="nav:BigModeNavigationEntry">
                <TextBlock Text="{Binding Title}" />
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>
</UserControl>
```

Entries expose `Kind`, typed `Key`, `Title`, `Description`, `CoverPath` and `LogoPath`; `PrimaryCoverPath`,
`PrimaryLogoPath` and `ItemLogoPath` support existing card templates. Node entries expose `Node` and
`ParentNode`; media entries expose `Item` and the actual `SourceNode`. Nodes precede direct media in existing
collection order. No descendant aggregation or synthetic nodes are performed. Binding changes selection;
`PlayCurrentCommand` opens nodes or activates media through the normal launcher. Nodes are previewed when
marked; theme ownership changes only when opening a node. Do not modify `Items` or toggle presentation flags
to navigate.

`CurrentNode` / `ThemeContextNode` describe the opened location/theme owner. `SelectedCategory` and
`SelectedItem` describe the mutually exclusive selected node/media. `NavigationBreadcrumb` is a localized,
display-only string (`Library › … › Opened node`) built from the session's existing opened frames. It changes
on location changes and path edits, not ordinary selection changes. Bind it to a single-line TextBlock with
`TextTrimming="LeadingCharacterEllipsis"` to preserve the current location in long paths. It does not expose
an alternative navigation stack or interactive breadcrumb commands.

`IsGameListActive` selects media preview panels, not a separate navigation mode. The old `Items` /
`CurrentCategories` remain direct-media/node
projections. Do not use either alone as a universal library list. Keep virtualized panels for large lists.

Wheel uses `WheelEntries` / `WheelSelectedEntryIndex` and its existing WheelPanel; Arcade retains its
media-only `CircularItems` and carousel recycling. Home uses the same navigation rules in its library row,
including direct media cards. Attract Mode only selects visible direct media in the current frame, even if
that frame also has nodes.

### 13.1 System host themes (BigMode themes with `SystemLayoutHost`)

A **system host theme** is just a normal BigMode theme stored under:

- `Themes/<HostName>/theme.axaml`

and recognized by the host because it contains a `ContentControl` named `SystemLayoutHost`:

```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:nav="clr-namespace:Retromind.Services.BigMode;assembly=Retromind" xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind" x:DataType="vm:BigModeViewModel" Background="Black"
         ext:ThemeProperties.Name="System Host (Classic)"
         ext:ThemeProperties.NavigationPresentation="NodeBrowser">
<Grid ColumnDefinitions="*,2*">

    <!-- LEFT: system list -->
    <Border Grid.Column="0"
            Background="#66000000"
            Padding="16"
            Margin="24,24,12,24">
        <ListBox ItemsSource="{Binding NavigationEntries}"
                 SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
                 SelectedIndex="{Binding SelectedEntryIndex, Mode=OneWay}"
                 BorderThickness="0"
                 Background="Transparent"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled">
            <ListBox.ItemTemplate>
                <DataTemplate x:DataType="nav:BigModeNavigationEntry">
                    <TextBlock Text="{Binding Title}"
                               FontSize="20"
                               Margin="0,4"
                               Foreground="White"
                               TextTrimming="CharacterEllipsis" />
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </Border>

    <!-- RIGHT: host slot for per-system subthemes -->
    <Border Grid.Column="1"
            Background="#22000000"
            Margin="12,24,24,24"
            CornerRadius="8">
        <ContentControl x:Name="SystemLayoutHost"
                        HorizontalAlignment="Stretch"
                        VerticalAlignment="Stretch" />
    </Border>
</Grid>
</UserControl>
```

Any BigMode theme that defines a `ContentControl x:Name="SystemLayoutHost"` is automatically treated as a **system host**:

- The host continues to provide `BigModeViewModel` as `DataContext`.
- The left side is up to the theme author (list, wheel, carousel, …).
- The right `SystemLayoutHost` contains a **system subtheme** for a selected node, or a host-owned media
  preview with the selected item's actual artwork, metadata and shared video channel.

> You can ship multiple host variants, e.g. `Themes/SystemHostClassic/theme.axaml`,  
> `Themes/SystemHostCarousel/theme.axaml`, …  
> Users can pick one per node via the normal „BigMode theme“ dropdown.

### 13.2 System subthemes (per-platform layouts)

System subthemes live under:

- `Themes/System/<Id>/theme.axaml`

Recommended structure:

- `Themes/System/Default/theme.axaml`
- `Themes/System/C64/theme.axaml`
- `Themes/System/SNES/theme.axaml`
...
- (optional: `Images/`, `Videos/` per folder)

The folder name `<Id>` is used as:

- the **technical id** (`SystemPreviewThemeId`) stored on `MediaNode`,
- the **key** in the system theme dropdown,
- part of the relative path `System/<Id>/theme.axaml` passed to `ThemeLoader`.

The host assigns `ext:ThemeProperties.SystemPreviewNode` to every system-subtheme
instance. Bind node-specific content through that attached property instead of the live
`SelectedCategory`. This keeps the outgoing theme's text and artwork stable during a
crossfade:

```xml
<UserControl x:Name="ThemeRoot">
    <TextBlock Text="{Binding ElementName=ThemeRoot,
                              Path=(ext:ThemeProperties.SystemPreviewNode).Name}" />
</UserControl>
```

A minimal subtheme:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions;assembly=Retromind"
             x:Name="ThemeRoot" x:DataType="vm:BigModeViewModel"
             Background="#101010"
             ext:ThemeProperties.Name="Default System Layout"
             ext:ThemeProperties.PrimaryVideoEnabled="True">
    <Grid ColumnDefinitions="*,2*" Margin="40">
        <StackPanel VerticalAlignment="Center" Spacing="12" Margin="0,0,32,0">
            <TextBlock Text="{Binding ElementName=ThemeRoot, Path=(ext:ThemeProperties.SystemPreviewNode).Name}"
                       FontSize="40" Foreground="White" TextWrapping="Wrap" />
            <TextBlock Text="{Binding ElementName=ThemeRoot, Path=(ext:ThemeProperties.SystemPreviewNode).Description}"
                       FontSize="20" Foreground="#CCCCCC" TextWrapping="Wrap"
                       MaxLines="4" TextTrimming="WordEllipsis" />
        </StackPanel>
        <Grid Grid.Column="1" Width="800" Height="450"
              HorizontalAlignment="Center" VerticalAlignment="Center" ClipToBounds="True">
            <ext:CrossfadeImage
                Url="{Binding ElementName=ThemeRoot, Path=(ext:ThemeProperties.SystemPreviewNode).PrimaryWallpaperAbsolutePath}"
                DecodeWidth="1280" Stretch="UniformToFill" LoadDelayMs="220" />
            <!-- Fallback artwork is a sibling; the host replaces only the slot child. -->
            <Border x:Name="VideoSlot" ClipToBounds="True" />
        </Grid>
    </Grid>
</UserControl>
```

Requirements for a subtheme:

- `x:DataType="vm:BigModeViewModel"`
- An empty `Border`, `ContentControl` or `Panel` named **`VideoSlot`** where the video preview should appear.
- Optional `ThemeProperties` for metadata / tuning.

### 13.3 Wiring: how the host selects subthemes

- For the active system host theme, `BigModeHostView`:
    - watches `BigModeViewModel.SelectedEntry`,
    - reads `SystemPreviewThemeId` from the selected `MediaNode`,
    - resolves it to a folder under `Themes/System/<Id>/`,
    - loads `System/<Id>/theme.axaml` through `ThemeLoader`,
    - sets the subtheme view as `Content` of `SystemLayoutHost`,
    - and uses the subtheme’s `VideoSlot` for the preview overlay.

`SystemPreviewThemeId` is stored on `MediaNode` and configured via the Node settings dialog.

If `SystemPreviewThemeId` is `null` or empty, the host falls back to `"Default"`:

- `Themes/System/Default/theme.axaml`

Both theme selections are local to their node. Neither `ThemePath` nor `SystemPreviewThemeId` is inherited from
a parent node. New settings and nodes use `Navigator/theme.axaml` as their normal theme. Explicitly saved
assignments, including `Default/theme.axaml`, remain unchanged. Missing or invalid normal theme paths fall
back to `Themes/Navigator/theme.axaml`, then `Themes/Default/theme.axaml` if Navigator is unavailable.
This fallback does not rewrite the saved assignment, including when unrelated application settings are saved.
The System subtheme default remains `Default`.

### 13.4 Selecting host + subthemes in the UI

In the node settings dialog:

- **BigMode theme**  
  → `ThemePath` (e.g. `Arcade/theme.axaml`, `Wheel/theme.axaml`, `System/theme.axaml`)

- **System-Theme (System-Auswahl)**  
  → `SystemPreviewThemeId` (`"Default"`, `"C64"`, `"SNES"`, …)

Typical setup:

- Root node „Games“ / „Spiele“:
    - BigMode theme: `System/theme.axaml`
    - The selected child node supplies the system-preview theme; this setting is independent of the opened node's regular theme.

- System nodes (C64, SNES, …):
    - BigMode theme: `Arcade/theme.axaml` (or any content theme)
    - System-Theme: `C64`, `SNES`, … (folder names under `Themes/System`)

This allows:

- Multiple **system host** layouts (scroll list, carousel, grid, …).
- Per-system **subthemes** for the preview area.

### 13.5 Publishing new system hosts and system subthemes

To publish an additional **system host theme**:

1. Create a folder under `Themes/`, e.g. `Themes/SystemHostCarousel/`.
2. Add `theme.axaml` with:
    - `x:DataType="vm:BigModeViewModel"`
    - a `ContentControl x:Name="SystemLayoutHost"` somewhere in your layout.
3. Ship the folder. Retromind will:
    - show `SystemHostCarousel/theme.axaml` in the BigMode theme dropdown,
    - treat it automatically as a system host theme.

To publish a new **system subtheme**:

1. Create a folder under `Themes/System/<Id>/`, e.g. `Themes/System/C64/`.
2. Add `theme.axaml` with:
    - `x:DataType="vm:BigModeViewModel"`
    - `Border x:Name="VideoSlot"` at the desired position.
3. Optionally set `ext:ThemeProperties.Name` on the root for a friendly display name.
4. Users can then pick `C64` in the „System-Theme (System-Auswahl)“ dropdown for the C64 node.
