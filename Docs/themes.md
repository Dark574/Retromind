# Retromind Themes (BigMode) — Theme Author Guide

Retromind supports **external `.axaml` themes** that are loaded **at runtime**.  
This allows anyone to create and publish themes without recompiling Retromind.

This guide covers:

- Theme structure and folder layout
- Conventions (slots / classes)
- `ThemeProperties` (metadata, sounds, tuning)
- Best practices, performance tips, and troubleshooting

---

## 1) What is a theme?

A theme is an Avalonia XAML file (typically a `UserControl`) loaded at runtime.  
Retromind sets the theme root’s `DataContext` to `BigModeViewModel`, so you can bind directly to it.

### Minimal theme skeleton
```xml 
<UserControl xmlns="[https://github.com/avaloniaui](https://github.com/avaloniaui)"
             xmlns:x="[http://schemas.microsoft.com/winfx/2006/xaml](http://schemas.microsoft.com/winfx/2006/xaml)"
             xmlns:vm="using:Retromind.ViewModels"
             x:DataType="vm:BigModeViewModel"
             Background="Black">
    <!-- Your layout -->
</UserControl>
```

---

## 2) Where do themes live? (recommended)

Themes typically live under:

- `Themes/<ThemeName>/theme.axaml`

Example:

- `Themes/Wheel/theme.axaml`

Ship your theme as a folder so users can install it by copying the directory.

Recommended structure:
- `Themes/MyTheme/theme.axaml`
- `sounds/navigate.wav, confirm.wav, cancel.wav`
- `images/ ...`

---

## 3) DataContext & bindings

The host automatically sets:

- `themeRoot.DataContext = BigModeViewModel`

So you can bind directly:

> Note: Some existing themes in Retromind use helpers (e.g. async image loading). That’s optional; you can use standard Avalonia controls if you prefer.

---

## 4) Video preview: Inline video controls (Main & Secondary)

Retromind renders videos via LibVLC into one or more `IVideoSurface` instances.  
Your theme can place these surfaces **directly** in the layout using `VideoSurfaceControl`, just like any other control.

There are currently two logical channels:

- **Main channel** – per-item/system preview (`MainVideoSurface`)
- **Secondary channel** – optional background / B‑Roll (`SecondaryVideoSurface`)

The `BigModeViewModel` exposes:

- `IVideoSurface? MainVideoSurface`
- `bool MainVideoHasContent`
- `bool MainVideoIsPlaying`

- `IVideoSurface? SecondaryVideoSurface`
- `bool SecondaryVideoHasContent`
- `bool SecondaryVideoIsPlaying`

> Important:
> - Themes never talk to LibVLC directly.
> - You only bind to these properties and decide **where** and **how** videos appear.

### 4.1 Main video (per-item preview)

Typical usage: show the preview video of the selected game or system in a dedicated area.

```xml
<Grid xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:video="using:Retromind.Helpers.Video" xmlns:helpers="using:Retromind.Helpers" x:DataType="vm:BigModeViewModel">
    <Grid.Resources>
        <helpers:BoolToOpacityConverter x:Key="BoolToOpacity"
                                        TrueOpacity="1.0"
                                        FalseOpacity="0.0" />
    </Grid.Resources>
    
    <Border Background="Black"
            CornerRadius="4"
            ClipToBounds="True">
        <Grid>
            <!-- Fallback text if no video is available for this item -->
            <TextBlock Text="Preview"
                       HorizontalAlignment="Center"
                       VerticalAlignment="Center"
                       Foreground="#404040"
                       FontSize="20"
                       IsVisible="{Binding MainVideoHasContent,
                                           Converter={x:Static helpers:ObjectConverters.IsNull}}"/>
    
            <!-- Inline video: main channel -->
            <video:VideoSurfaceControl Surface="{Binding MainVideoSurface}"
                                       Stretch="UniformToFill"
                                       IsVisible="{Binding MainVideoHasContent}"
                                       Opacity="{Binding MainVideoIsPlaying,
                                                         Converter={StaticResource BoolToOpacity}}"/>
        </Grid>
    </Border>
</Grid>
```

- `MainVideoHasContent` → `true` if the current selection has a valid preview video and video mode is enabled.
- `MainVideoIsPlaying` → `true` while the main player is actually running.

You are free to:

- put overlays (bezels, CRT masks, scanlines, etc.) **above** the video,
- combine the video with reactive effects (opacity, blur, glow) based on `MainVideoIsPlaying`.

### 4.2 Secondary video (background / B‑Roll)

The secondary channel is intended for theme‑level videos, for example:

- a moving background (wallpaper),
- a small system intro loop,
- decorative B‑Roll.

The pipeline is:

1. The theme root specifies a **theme‑local relative path** via `ThemeProperties.SecondaryBackgroundVideoPath`, e.g.:
```xml
<UserControl    xmlns:ext="clr-namespace:Retromind.Extensions"
                ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/NameOfVideo.mp4">
```

This path is relative to the theme folder (e.g. `Themes/Arcade/Videos/bkg_anim.mp4`).

2. `ThemeLoader` reads that property and stores it in `Theme.SecondaryBackgroundVideoPath`.

3. `BigModeViewModel` resolves `BasePath + SecondaryBackgroundVideoPath`, loads the video into a second LibVLC player and exposes it via:

    - `SecondaryVideoSurface`
    - `SecondaryVideoHasContent`
    - `SecondaryVideoIsPlaying`

Example: full‑screen video wallpaper with static fallback image:
```xml
<UserControl xmlns:video="using:Retromind.Helpers.Video" 
             xmlns:helpers="using:Retromind.Helpers" 
             xmlns:ext="clr-namespace:Retromind.Extensions" 
             x:DataType="vm:BigModeViewModel" 
             Background="Black" 
             
             ext:ThemeProperties.Name="Arcade" 
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
            <video:VideoSurfaceControl Surface="{Binding SecondaryVideoSurface}"
                                       Stretch="UniformToFill"
                                       IsVisible="{Binding SecondaryVideoHasContent}"/>
    
            <!-- Static fallback wallpaper when no background video is available -->
            <Image Source="{Binding Converter={StaticResource ThemeAssetConverter},
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

- If the configured video file exists → `SecondaryVideoHasContent = true` → video is drawn, image fades out.
- If it does not exist (or video is disabled) → no secondary video, only the static wallpaper is visible.

---

## 5) ThemeProperties: “manifest” + tuning API

`ThemeProperties` are **attached properties** meant to be set on the theme root.  
They act as a theme “manifest” (metadata/sounds) and as a tuning API (UX/animation/typography/layout).

Example:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions"
             x:DataType="vm:BigModeViewModel"
             Background="Black"

             ext:ThemeProperties.Name="Wheel"
             ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
             ext:ThemeProperties.Version="1.0.0"
             ext:ThemeProperties.WebsiteUrl="https://example.invalid"

             ext:ThemeProperties.AccentColor="#FFD700"
             ext:ThemeProperties.VideoEnabled="True"
             ext:ThemeProperties.VideoSlotName="VideoSlot"

             ext:ThemeProperties.SelectedScale="1.10"
             ext:ThemeProperties.UnselectedOpacity="0.60"
             ext:ThemeProperties.SelectedGlowOpacity="0.55"

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

These are useful for a future theme browser, diagnostics overlays, and proper crediting.

### 6.2 Sounds (optional)

- `ThemeProperties.NavigateSound` (string?)
- `ThemeProperties.ConfirmSound` (string?)
- `ThemeProperties.CancelSound` (string?)

Example:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions"
             x:DataType="vm:BigModeViewModel"
             ext:ThemeProperties.NavigateSound="sounds/navigate.wav"
             ext:ThemeProperties.ConfirmSound="sounds/confirm.wav"
             ext:ThemeProperties.CancelSound="sounds/cancel.wav">
    <!-- Theme layout -->
</UserControl>
```

> Tip: Use paths relative to your theme folder.

### 6.3 Video

- `ThemeProperties.VideoEnabled` (bool, default: `true`)  
  Enables/disables all video playback for this theme (both main and secondary channels). If false, the ViewModel will not start LibVLC preview playback for this theme.
- `ThemeProperties.SecondaryBackgroundVideoPath` (string?, optional)  
  Theme-local relative path to a background video for the secondary channel.
  Example: "Videos/NameOfVideo.mp4" → resolved as Path.Combine(BasePath, "Videos/NameOfVideo.mp4").

Example:
```xml
<UserControl xmlns:ext="clr-namespace:Retromind.Extensions"
             ext:ThemeProperties.VideoEnabled="True"
             ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4">
             <!-- layout -->
</UserControl>
```

### 6.4 Tuning: selection/focus (controller-friendly)

These values are used by the host to produce consistent “TV-like” selection behavior.

- `ThemeProperties.SelectedScale` (double, default: `1.06`)
- `ThemeProperties.UnselectedOpacity` (double, default: `0.75`)
- `ThemeProperties.SelectedGlowOpacity` (double, default: `0.35`)
- `ThemeProperties.AccentColor` (Color?) — used for the glow color when set (fallback: gold)

Recommended ranges:

- `SelectedScale`: 1.05–1.15
- `UnselectedOpacity`: 0.45–0.80
- `SelectedGlowOpacity`: 0.20–0.70

### 6.5 Tuning: animations

- `ThemeProperties.FadeDurationMs` (int, default: `200`)
- `ThemeProperties.MoveDurationMs` (int, default: `160`)

“Snappy” UI: ~120–180ms  
“Cinematic” UI: ~220–320ms

### 6.6 Tuning: layout

- `ThemeProperties.PanelPadding` (Thickness, default: `20`)
- `ThemeProperties.HeaderSpacing` (double, default: `10`)
- `ThemeProperties.TileSize` (double, default: `220`)
- `ThemeProperties.TileSpacing` (double, default: `12`)
- `ThemeProperties.TileCornerRadius` (CornerRadius, default: `12`)
- `ThemeProperties.TileBorderThickness` (Thickness, default: `0`)

Not every theme must use every value; many are intended as shared knobs.

### 6.7 Tuning: typography (TV-friendly defaults)

- `ThemeProperties.TitleFontSize` (double, default: `34`)
- `ThemeProperties.BodyFontSize` (double, default: `18`)
- `ThemeProperties.CaptionFontSize` (double, default: `14`)

### 6.8 Attract Mode (auto-random selection on idle)

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
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:ext="clr-namespace:Retromind.Extensions" x:DataType="vm:BigModeViewModel" Background="Black"
         ext:ThemeProperties.Name="Arcade"
         ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
         ext:ThemeProperties.Version="0.1.0"
         ext:ThemeProperties.VideoEnabled="True"
         ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4"
         ext:ThemeProperties.AttractModeEnabled="True"
         ext:ThemeProperties.AttractModeIdleSeconds="60"
         ext:ThemeProperties.AttractModeSound="sounds/attract_spin.wav">
</UserControl>
```


Details:

- Attract mode only operates while **game list view** is active
  (`IsGameListActive == true`) and `Items.Count > 0`.
- Selections are performed using Retromind’s `RandomHelper`; the ViewModel
  simply moves the selection to a random item in the current list.
- Any user input (navigation, select, back) resets the idle timer and the
  internal step counter.

Example (Arcade theme):
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:ext="clr-namespace:Retromind.Extensions" x:DataType="vm:BigModeViewModel" Background="Black"
         ext:ThemeProperties.Name="Arcade"
         ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
         ext:ThemeProperties.Version="0.1.0"
         ext:ThemeProperties.VideoEnabled="True"
         ext:ThemeProperties.SecondaryBackgroundVideoPath="Videos/bkg_anim.mp4"
         ext:ThemeProperties.AttractModeEnabled="True"
         ext:ThemeProperties.AttractModeIdleSeconds="60">

</UserControl>
```

In this example:

- After 60 seconds of no input in game view, one random game is selected.
- After 120 seconds, another random game is selected.
- After 180 seconds, a third one, and so on — until the user interacts again.

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

#### `rm-title`, `rm-body`, `rm-caption`
Apply default font sizes to `TextBlock` elements:

- `rm-title` → `TitleFontSize`
- `rm-body` → `BodyFontSize`
- `rm-caption` → `CaptionFontSize`

Example:
```xml
<TextBlock Classes="rm-title"
           Text="{Binding SelectedItem.Title}"
           FontWeight="Bold"/>

<TextBlock Classes="rm-body"
           Text="{Binding SelectedItem.Description}"
           TextWrapping="Wrap"/>

<TextBlock Classes="rm-caption"
           Text="Press A to start"/>
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
- Large libraries can have many items.
- Avoid extremely heavy effects on every item (big blur radius everywhere can be expensive).
- Let the host handle selection UX when possible (don’t duplicate work in your theme).

### 8.3 Video slot must not be 0×0
If your slot has no meaningful size yet, the host may hide the overlay to avoid glitchy full-screen placement.

---

## 9) Troubleshooting

### Video does not show up
Checklist:
1. `ext:ThemeProperties.VideoEnabled="True"` (or omitted → defaults to true)
2. Slot exists:
    - default: `x:Name="VideoSlot"`
    - or custom `VideoSlotName` + matching `x:Name`
3. Slot has a valid size (width/height > 0)

### Selection visuals look odd
If you apply very strong custom styles to `ListBoxItem`, it may conflict with host tuning.  
Either rely on host selection defaults, or intentionally tune your styles to match.

---

## 10) Example theme: tuning + hooks

```xml
<UserControl xmlns="https://github.com/avaloniaui" 
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" 
             xmlns:vm="using:Retromind.ViewModels" 
             xmlns:ext="clr-namespace:Retromind.Extensions" 
             x:DataType="vm:BigModeViewModel" 
             Background="Black">

             ext:ThemeProperties.Name="MinimalTV"
             ext:ThemeProperties.Author="PLACEHOLDER_AUTHOR"
             ext:ThemeProperties.Version="1.0.0"
        
             ext:ThemeProperties.AccentColor="#00D1FF"
             ext:ThemeProperties.SelectedScale="1.12"
             ext:ThemeProperties.UnselectedOpacity="0.55"
             ext:ThemeProperties.SelectedGlowOpacity="0.50"
             ext:ThemeProperties.FadeDurationMs="180"
             ext:ThemeProperties.MoveDurationMs="180"
             ext:ThemeProperties.PanelPadding="28"
             ext:ThemeProperties.TitleFontSize="52"
             ext:ThemeProperties.BodyFontSize="20">
        
             <Grid ColumnDefinitions="420,*">
                <Border Classes="rm-panel">
                    <StackPanel Classes="rm-header">
                        <TextBlock Classes="rm-title" Text="{Binding CategoryTitle}" FontWeight="Bold"/>
                        <TextBlock Classes="rm-caption" Text="Use D-Pad / Stick to navigate" Opacity="0.8"/>
                    </StackPanel>
            
                    <ListBox ItemsSource="{Binding Items}"
                             SelectedItem="{Binding SelectedItem}"
                             Margin="0,18,0,0"/>
                </Border>
            
                <Border Classes="rm-panel" Grid.Column="1">
                    <StackPanel>
                        <TextBlock Classes="rm-title" Text="{Binding SelectedItem.Title}" FontWeight="Bold"/>
                        <TextBlock Classes="rm-body" Text="{Binding SelectedItem.Description}" TextWrapping="Wrap" Margin="0,18,0,0"/>
            
                        <Border x:Name="VideoSlot"
                                Width="800" Height="450"
                                Margin="0,24,0,0"
                                Background="#00000000"/>
                    </StackPanel>
                </Border>
             </Grid>
</UserControl>
```
---

## 11) Versioning & compatibility

- ThemeProperties are designed to be optional; missing values fall back to defaults.
- If a theme uses properties unknown to an older Retromind version, it may fail to load.
    - Keep `ThemeProperties.Version` up to date.
    - When publishing a theme, mention the minimum supported Retromind version.

## 12) System browser themes (platform selection)

Retromind supports a **two-level theming model** for the platform selection in BigMode:

1. A **system host theme** – the main BigMode theme used on the „Systems“ level  
   (e.g. a list of platforms on the left, preview on the right).
2. **System subthemes** – per-platform layouts that are embedded into the host on the right side  
   (e.g. different TV/mockups for C64, SNES, PC, …).

This section describes how to build both.

### 12.1 System host themes (BigMode themes with `SystemLayoutHost`)

A **system host theme** is just a normal BigMode theme stored under:

- `Themes/<HostName>/theme.axaml`

and recognized by the host because it contains a `ContentControl` named `SystemLayoutHost`:

```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:ext="clr-namespace:Retromind.Extensions" x:DataType="vm:BigModeViewModel" Background="Black"
         ext:ThemeProperties.Name="System Host (Classic)">
<Grid ColumnDefinitions="*,2*">

    <!-- LEFT: system list -->
    <Border Grid.Column="0"
            Background="#66000000"
            Padding="16"
            Margin="24,24,12,24">
        <ListBox ItemsSource="{Binding CurrentCategories}"
                 SelectedItem="{Binding SelectedCategory}"
                 BorderThickness="0"
                 Background="Transparent"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <TextBlock Text="{Binding Name}"
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
- The right `SystemLayoutHost` is filled by the host with a **system subtheme** based on the selected node.

> You can ship multiple host variants, e.g. `Themes/SystemHostClassic/theme.axaml`,  
> `Themes/SystemHostCarousel/theme.axaml`, …  
> Users can pick one per node via the normal „BigMode theme“ dropdown.

### 12.2 System subthemes (per-platform layouts)

System subthemes live under:

- `Themes/System/<Id>/theme.axaml`

Recommended structure:

- `Themes/System/Default/theme.axaml`
- `Themes/System/C64/theme.axaml`
- `Themes/System/SNES/theme.axaml`
- (optional: `Images/`, `Videos/` per folder)

The folder name `<Id>` is used as:

- the **technical id** (`SystemPreviewThemeId`) stored on `MediaNode`,
- the **key** in the system theme dropdown,
- part of the relative path `System/<Id>/theme.axaml` passed to `ThemeLoader`.

A minimal subtheme:
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="using:Retromind.ViewModels" xmlns:ext="clr-namespace:Retromind.Extensions" x:DataType="vm:BigModeViewModel" Background="#101010"
         ext:ThemeProperties.Name="Default System Layout"
         ext:ThemeProperties.VideoEnabled="True">

<Grid RowDefinitions="*,Auto" ColumnDefinitions="*,2*" Margin="40">

    <!-- LEFT: system info -->
    <StackPanel Grid.Row="0" Grid.Column="0" VerticalAlignment="Center" Spacing="8">
        <TextBlock Text="{Binding SelectedCategory.Name}"
                   FontSize="40"
                   FontWeight="Bold"
                   Foreground="White"
                   TextTrimming="CharacterEllipsis" />

        <TextBlock Text="{Binding CategoryTitle}"
                   FontSize="16"
                   Foreground="#CCCCCC"
                   TextWrapping="Wrap"
                   MaxWidth="400" />
    </StackPanel>

    <!-- RIGHT: simple monitor with VideoSlot -->
    <Grid Grid.Row="0" Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center">
        <Border Background="#181818" CornerRadius="16" Padding="16">
            <Grid RowDefinitions="Auto,Auto">

                <Border Grid.Row="0"
                        Background="#000000"
                        CornerRadius="8"
                        Padding="4"
                        Margin="0,0,0,12">
                    <!-- VideoSlot: the host will place the VLC overlay here -->
                    <Border x:Name="VideoSlot"
                            Background="#000000"
                            CornerRadius="4"
                            ClipToBounds="True"
                            MinWidth="480"
                            MinHeight="270">
                        <TextBlock Text="No preview video"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"
                                   Foreground="#505050"
                                   FontSize="18" />
                    </Border>
                </Border>

                <StackPanel Grid.Row="1"
                            Orientation="Horizontal"
                            HorizontalAlignment="Right"
                            Spacing="12">
                    <StackPanel Orientation="Horizontal" Spacing="4">
                        <TextBlock Text="Games:" Foreground="#AAAAAA" FontSize="14" />
                        <TextBlock Text="{Binding TotalGames}" Foreground="#FFFFFF" FontSize="14" />
                    </StackPanel>

                    <StackPanel Orientation="Horizontal" Spacing="4">
                        <TextBlock Text="Year:" Foreground="#AAAAAA" FontSize="14" />
                        <TextBlock Text="{Binding SelectedYear}" Foreground="#FFFFFF" FontSize="14" />
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Border>
    </Grid>
</Grid>
</UserControl>
```

Requirements for a subtheme:

- `x:DataType="vm:BigModeViewModel"`
- A `Border` (or any control) named **`VideoSlot`** where the video preview should appear.
- Optional `ThemeProperties` for metadata / tuning.

### 12.3 Wiring: how the host selects subthemes

- For the active system host theme, `BigModeHostView`:
    - watches `BigModeViewModel.SelectedCategory`,
    - reads `SystemPreviewThemeId` from the selected `MediaNode`,
    - resolves it to a folder under `Themes/System/<Id>/`,
    - loads `System/<Id>/theme.axaml` through `ThemeLoader`,
    - sets the subtheme view as `Content` of `SystemLayoutHost`,
    - and uses the subtheme’s `VideoSlot` for the preview overlay.

`SystemPreviewThemeId` is stored on `MediaNode` and configured via the Node settings dialog.

If `SystemPreviewThemeId` is `null` or empty, the host falls back to `"Default"`:

- `Themes/System/Default/theme.axaml`

### 12.4 Selecting host + subthemes in the UI

In the node settings dialog:

- **BigMode theme**  
  → `ThemePath` (e.g. `Arcade/theme.axaml`, `Wheel/theme.axaml`, `SystemHostClassic/theme.axaml`)

- **System-Theme (System-Auswahl)**  
  → `SystemPreviewThemeId` (`"Default"`, `"C64"`, `"SNES"`, …)

Typical setup:

- Root node „Games“ / „Spiele“:
    - BigMode theme: `SystemHostClassic/theme.axaml`
    - System-Theme: `(disabled)`

- System nodes (C64, SNES, …):
    - BigMode theme: `Arcade/theme.axaml` (or any content theme)
    - System-Theme: `C64`, `SNES`, … (folder names under `Themes/System`)

This allows:

- Multiple **system host** layouts (scroll list, carousel, grid, …).
- Per-system **subthemes** for the preview area.

### 12.5 Publishing new system hosts and system subthemes

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