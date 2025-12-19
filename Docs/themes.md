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

## 4) Video preview: host overlay + theme “slot”

Retromind renders preview video via a **stable host overlay** (LibVLC).  
Your theme only defines a **slot** (a placeholder control) to tell the host where to place the video.

### Default slot name: `VideoSlot`
```xml
<Border x:Name="VideoSlot"
        Width="854"
        Height="480"
        Background="Transparent"/>
```

### Optional: rename the slot

### Rename the slot (`VideoSlotName`)

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions"
             x:DataType="vm:BigModeViewModel"
             ext:ThemeProperties.VideoSlotName="PreviewVideo">
  <Border x:Name="PreviewVideo"
          Width="854"
          Height="480"
          Background="Transparent"/>
</UserControl>
```

### Disable video

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Retromind.ViewModels"
             xmlns:ext="clr-namespace:Retromind.Extensions"
             x:DataType="vm:BigModeViewModel"
             ext:ThemeProperties.VideoEnabled="False">
  <!-- Layout without video -->
</UserControl>
```

> Video is shown only if `VideoEnabled=true` and the slot exists and has a meaningful size.

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
- `ThemeProperties.VideoSlotName` (string, default: `"VideoSlot"`)

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
