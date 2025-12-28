using System;
using Avalonia;
using Avalonia.Media;

namespace Retromind.Extensions;

public class ThemeProperties : AvaloniaObject
{
    // Sound for navigation actions
    public static readonly AttachedProperty<string?> NavigateSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("NavigateSound");

    public static string? GetNavigateSound(AvaloniaObject element) => element.GetValue(NavigateSoundProperty);
    public static void SetNavigateSound(AvaloniaObject element, string? value) => element.SetValue(NavigateSoundProperty, value);

    // Sound for confirmation actions
    public static readonly AttachedProperty<string?> ConfirmSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("ConfirmSound");
    
    public static string? GetConfirmSound(AvaloniaObject element) => element.GetValue(ConfirmSoundProperty);
    public static void SetConfirmSound(AvaloniaObject element, string? value) => element.SetValue(ConfirmSoundProperty, value);

    // Sound for cancel/back actions
    public static readonly AttachedProperty<string?> CancelSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("CancelSound");
    
    public static string? GetCancelSound(AvaloniaObject element) => element.GetValue(CancelSoundProperty);
    public static void SetCancelSound(AvaloniaObject element, string? value) => element.SetValue(CancelSoundProperty, value);
    
    // --- Video capability toggle ---
    // If false, the host will disable the video overlay even if a VideoSlot exists.
    public static readonly AttachedProperty<bool> VideoEnabledProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>("VideoEnabled", defaultValue: true);

    public static bool GetVideoEnabled(AvaloniaObject element) => element.GetValue(VideoEnabledProperty);
    public static void SetVideoEnabled(AvaloniaObject element, bool value) => element.SetValue(VideoEnabledProperty, value);

    // --- Theme metadata (optional, for UI/diagnostics) ---

    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Name");

    public static string? GetName(AvaloniaObject element) => element.GetValue(NameProperty);
    public static void SetName(AvaloniaObject element, string? value) => element.SetValue(NameProperty, value);

    public static readonly AttachedProperty<string?> AuthorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Author");

    public static string? GetAuthor(AvaloniaObject element) => element.GetValue(AuthorProperty);
    public static void SetAuthor(AvaloniaObject element, string? value) => element.SetValue(AuthorProperty, value);

    public static readonly AttachedProperty<string?> VersionProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Version");

    public static string? GetVersion(AvaloniaObject element) => element.GetValue(VersionProperty);
    public static void SetVersion(AvaloniaObject element, string? value) => element.SetValue(VersionProperty, value);

    public static readonly AttachedProperty<string?> WebsiteUrlProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("WebsiteUrl");

    public static string? GetWebsiteUrl(AvaloniaObject element) => element.GetValue(WebsiteUrlProperty);
    public static void SetWebsiteUrl(AvaloniaObject element, string? value) => element.SetValue(WebsiteUrlProperty, value);

    // --- Slot conventions (optional) ---
    // Default keeps current behavior: "VideoSlot"
    public static readonly AttachedProperty<string> VideoSlotNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>("VideoSlotName", defaultValue: "VideoSlot");

    public static string GetVideoSlotName(AvaloniaObject element) => element.GetValue(VideoSlotNameProperty);
    public static void SetVideoSlotName(AvaloniaObject element, string value) => element.SetValue(VideoSlotNameProperty, value);

    // --- Theme tuning (Layout / Animation / Typography) ---
    // These values are intended to be set on the theme root element and consumed by
    // styles, converters, or the host to provide consistent UX across themes.

    // Spacing / sizing
    public static readonly AttachedProperty<double> TileSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("TileSize", defaultValue: 220);

    public static double GetTileSize(AvaloniaObject element) => element.GetValue(TileSizeProperty);
    public static void SetTileSize(AvaloniaObject element, double value) => element.SetValue(TileSizeProperty, value);

    public static readonly AttachedProperty<double> TileSpacingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("TileSpacing", defaultValue: 12);

    public static double GetTileSpacing(AvaloniaObject element) => element.GetValue(TileSpacingProperty);
    public static void SetTileSpacing(AvaloniaObject element, double value) => element.SetValue(TileSpacingProperty, value);

    public static readonly AttachedProperty<Thickness> PanelPaddingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>("PanelPadding", defaultValue: new Thickness(20));

    public static Thickness GetPanelPadding(AvaloniaObject element) => element.GetValue(PanelPaddingProperty);
    public static void SetPanelPadding(AvaloniaObject element, Thickness value) => element.SetValue(PanelPaddingProperty, value);

    public static readonly AttachedProperty<double> HeaderSpacingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("HeaderSpacing", defaultValue: 10);

    public static double GetHeaderSpacing(AvaloniaObject element) => element.GetValue(HeaderSpacingProperty);
    public static void SetHeaderSpacing(AvaloniaObject element, double value) => element.SetValue(HeaderSpacingProperty, value);

    // Shape / borders
    public static readonly AttachedProperty<CornerRadius> TileCornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, CornerRadius>("TileCornerRadius", defaultValue: new CornerRadius(12));

    public static CornerRadius GetTileCornerRadius(AvaloniaObject element) => element.GetValue(TileCornerRadiusProperty);
    public static void SetTileCornerRadius(AvaloniaObject element, CornerRadius value) => element.SetValue(TileCornerRadiusProperty, value);

    public static readonly AttachedProperty<Thickness> TileBorderThicknessProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>("TileBorderThickness", defaultValue: new Thickness(0));

    public static Thickness GetTileBorderThickness(AvaloniaObject element) => element.GetValue(TileBorderThicknessProperty);
    public static void SetTileBorderThickness(AvaloniaObject element, Thickness value) => element.SetValue(TileBorderThicknessProperty, value);

    // Focus/selection behavior (controller-friendly)
    public static readonly AttachedProperty<double> SelectedScaleProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("SelectedScale", defaultValue: 1.06);

    public static double GetSelectedScale(AvaloniaObject element) => element.GetValue(SelectedScaleProperty);
    public static void SetSelectedScale(AvaloniaObject element, double value) => element.SetValue(SelectedScaleProperty, value);

    public static readonly AttachedProperty<double> UnselectedOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("UnselectedOpacity", defaultValue: 0.75);

    public static double GetUnselectedOpacity(AvaloniaObject element) => element.GetValue(UnselectedOpacityProperty);
    public static void SetUnselectedOpacity(AvaloniaObject element, double value) => element.SetValue(UnselectedOpacityProperty, value);

    public static readonly AttachedProperty<double> SelectedGlowOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("SelectedGlowOpacity", defaultValue: 0.35);

    public static double GetSelectedGlowOpacity(AvaloniaObject element) => element.GetValue(SelectedGlowOpacityProperty);
    public static void SetSelectedGlowOpacity(AvaloniaObject element, double value) => element.SetValue(SelectedGlowOpacityProperty, value);

    /// <summary>
    /// Steuert, ob der BigMode-Host generische Auswahl-Effekte (Zoom/Opacity/Glow)
    /// für diese ListBox anwenden darf. Standard: true.
    /// Themes, die ihre eigenen Effekte pro Item rendern (z.B. ArcadeLogoList),
    /// können diesen Wert auf false setzen.
    /// </summary>
    public static readonly AttachedProperty<bool> UseHostSelectionEffectsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "UseHostSelectionEffects",
            defaultValue: true);

    public static bool GetUseHostSelectionEffects(AvaloniaObject element) =>
        element.GetValue(UseHostSelectionEffectsProperty);

    public static void SetUseHostSelectionEffects(AvaloniaObject element, bool value) =>
        element.SetValue(UseHostSelectionEffectsProperty, value);

    // Overlays / background
    public static readonly AttachedProperty<double> BackgroundDimOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("BackgroundDimOpacity", defaultValue: 0.35);

    public static double GetBackgroundDimOpacity(AvaloniaObject element) => element.GetValue(BackgroundDimOpacityProperty);
    public static void SetBackgroundDimOpacity(AvaloniaObject element, double value) => element.SetValue(BackgroundDimOpacityProperty, value);

    public static readonly AttachedProperty<double> PanelBackgroundOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("PanelBackgroundOpacity", defaultValue: 0.18);

    public static double GetPanelBackgroundOpacity(AvaloniaObject element) => element.GetValue(PanelBackgroundOpacityProperty);
    public static void SetPanelBackgroundOpacity(AvaloniaObject element, double value) => element.SetValue(PanelBackgroundOpacityProperty, value);

    // Animation timings (milliseconds; easy for theme authors)
    public static readonly AttachedProperty<int> FadeDurationMsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>("FadeDurationMs", defaultValue: 200);

    public static int GetFadeDurationMs(AvaloniaObject element) => element.GetValue(FadeDurationMsProperty);
    public static void SetFadeDurationMs(AvaloniaObject element, int value) => element.SetValue(FadeDurationMsProperty, value);

    public static readonly AttachedProperty<int> MoveDurationMsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>("MoveDurationMs", defaultValue: 160);

    public static int GetMoveDurationMs(AvaloniaObject element) => element.GetValue(MoveDurationMsProperty);
    public static void SetMoveDurationMs(AvaloniaObject element, int value) => element.SetValue(MoveDurationMsProperty, value);

    // --- Generische Animations-Slots (Primary/Secondary/Background Visuals) ---

    /// <summary>
    /// Name des primären visuellen Elements im Theme (z.B. Cover, Hauptlogo, Cabinet-Screen).
    /// Der Host kann dieses Element anhand des Namens finden und animieren.
    /// </summary>
    public static readonly AttachedProperty<string> PrimaryVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "PrimaryVisualElementName",
            defaultValue: "PrimaryVisual");

    public static string GetPrimaryVisualElementName(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualElementNameProperty);

    public static void SetPrimaryVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(PrimaryVisualElementNameProperty, value);

    /// <summary>
    /// Einfache Kennzeichnung, wie das primäre Visual beim Wechsel eingeblendet werden soll.
    /// Unterstützte Werte (Konvention, vom Host interpretiert): "None", "Fade", "SlideFromLeft",
    /// "SlideFromRight", "SlideFromTop", "SlideFromBottom".
    /// </summary>
    public static readonly AttachedProperty<string> PrimaryVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "PrimaryVisualEnterMode",
            defaultValue: "None");

    public static string GetPrimaryVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterModeProperty);

    public static void SetPrimaryVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(PrimaryVisualEnterModeProperty, value);

    /// <summary>
    /// Horizontaler Startversatz für das primäre Visual (in Pixeln).
    /// Negativ = von links, positiv = von rechts. Wird nur von Slide-Modi genutzt.
    /// </summary>
    public static readonly AttachedProperty<double> PrimaryVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "PrimaryVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetPrimaryVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterOffsetXProperty);

    public static void SetPrimaryVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(PrimaryVisualEnterOffsetXProperty, value);

    /// <summary>
    /// Vertikaler Startversatz für das primäre Visual (in Pixeln).
    /// Negativ = von oben, positiv = von unten. Wird nur von Slide-Modi genutzt.
    /// </summary>
    public static readonly AttachedProperty<double> PrimaryVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "PrimaryVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetPrimaryVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterOffsetYProperty);

    public static void SetPrimaryVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(PrimaryVisualEnterOffsetYProperty, value);

    // --- SECONDARY VISUAL ---

    /// <summary>
    /// Name des sekundären visuellen Elements (z.B. Logo, Titel-Textbereich).
    /// </summary>
    public static readonly AttachedProperty<string> SecondaryVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "SecondaryVisualElementName",
            defaultValue: "SecondaryVisual");

    public static string GetSecondaryVisualElementName(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualElementNameProperty);

    public static void SetSecondaryVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(SecondaryVisualElementNameProperty, value);

    /// <summary>
    /// Modus für die Einblendung des sekundären Visuals. Gleiche Werte wie beim PrimaryVisualEnterMode.
    /// </summary>
    public static readonly AttachedProperty<string> SecondaryVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "SecondaryVisualEnterMode",
            defaultValue: "None");

    public static string GetSecondaryVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterModeProperty);

    public static void SetSecondaryVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(SecondaryVisualEnterModeProperty, value);

    public static readonly AttachedProperty<double> SecondaryVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SecondaryVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetSecondaryVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterOffsetXProperty);

    public static void SetSecondaryVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(SecondaryVisualEnterOffsetXProperty, value);

    public static readonly AttachedProperty<double> SecondaryVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SecondaryVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetSecondaryVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterOffsetYProperty);

    public static void SetSecondaryVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(SecondaryVisualEnterOffsetYProperty, value);

    // --- BACKGROUND VISUAL ---

    /// <summary>
    /// Name des Hintergrund-Visuals, das beim Item-Wechsel animiert werden soll
    /// (z.B. Wallpaper-Image, großer Hintergrund-Container).
    /// </summary>
    public static readonly AttachedProperty<string> BackgroundVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "BackgroundVisualElementName",
            defaultValue: "BackgroundVisual");

    public static string GetBackgroundVisualElementName(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualElementNameProperty);

    public static void SetBackgroundVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(BackgroundVisualElementNameProperty, value);

    /// <summary>
    /// Modus für die Einblendung des Hintergrund-Visuals (Fade/Slide...).
    /// </summary>
    public static readonly AttachedProperty<string> BackgroundVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "BackgroundVisualEnterMode",
            defaultValue: "None");

    public static string GetBackgroundVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterModeProperty);

    public static void SetBackgroundVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(BackgroundVisualEnterModeProperty, value);

    public static readonly AttachedProperty<double> BackgroundVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BackgroundVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetBackgroundVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterOffsetXProperty);

    public static void SetBackgroundVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(BackgroundVisualEnterOffsetXProperty, value);

    public static readonly AttachedProperty<double> BackgroundVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BackgroundVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetBackgroundVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterOffsetYProperty);

    public static void SetBackgroundVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(BackgroundVisualEnterOffsetYProperty, value);

    // Typography (defaults tuned for “TV distance”)
    public static readonly AttachedProperty<double> TitleFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("TitleFontSize", defaultValue: 34);

    public static double GetTitleFontSize(AvaloniaObject element) => element.GetValue(TitleFontSizeProperty);
    public static void SetTitleFontSize(AvaloniaObject element, double value) => element.SetValue(TitleFontSizeProperty, value);

    public static readonly AttachedProperty<double> BodyFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("BodyFontSize", defaultValue: 18);

    public static double GetBodyFontSize(AvaloniaObject element) => element.GetValue(BodyFontSizeProperty);
    public static void SetBodyFontSize(AvaloniaObject element, double value) => element.SetValue(BodyFontSizeProperty, value);

    public static readonly AttachedProperty<double> CaptionFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>("CaptionFontSize", defaultValue: 14);

    public static double GetCaptionFontSize(AvaloniaObject element) => element.GetValue(CaptionFontSizeProperty);
    public static void SetCaptionFontSize(AvaloniaObject element, double value) => element.SetValue(CaptionFontSizeProperty, value);

    // Optional visual hints
    public static readonly AttachedProperty<Color?> AccentColorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Color?>("AccentColor");

    public static Color? GetAccentColor(AvaloniaObject element) => element.GetValue(AccentColorProperty);
    public static void SetAccentColor(AvaloniaObject element, Color? value) => element.SetValue(AccentColorProperty, value);
    
    // --- Theme file system helpers (portable themes) ---
    // These helpers are intended for theme authors to easily reference images and sounds
    // that live inside the theme directory next to the AppImage.
    // The host (ThemeLoader) is responsible for setting ThemeBasePath when a theme is loaded.

    /// <summary>
    /// Enables the "Attract Mode" for this theme.
    /// When true, the host may automatically scroll/select random items
    /// after a period of user inactivity (see AttractModeIdleSeconds).
    /// </summary>
    public static readonly AttachedProperty<bool> AttractModeEnabledProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "AttractModeEnabled",
            defaultValue: false);

    public static bool GetAttractModeEnabled(AvaloniaObject element) =>
        element.GetValue(AttractModeEnabledProperty);

    public static void SetAttractModeEnabled(AvaloniaObject element, bool value) =>
        element.SetValue(AttractModeEnabledProperty, value);

    /// <summary>
    /// Idle time in seconds before Attract Mode performs the first random selection.
    /// Every additional multiple of this interval will trigger another random selection
    /// while the user remains inactive. A value of 0 disables the timer.
    /// </summary>
    public static readonly AttachedProperty<int> AttractModeIdleSecondsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "AttractModeIdleSeconds",
            defaultValue: 0);

    public static int GetAttractModeIdleSeconds(AvaloniaObject element) =>
        element.GetValue(AttractModeIdleSecondsProperty);

    public static void SetAttractModeIdleSeconds(AvaloniaObject element, int value) =>
        element.SetValue(AttractModeIdleSecondsProperty, value);

    /// <summary>
    /// Optional theme-local sound that is played when Attract Mode kicks in
    /// (e.g. a short spin / "roulette" sound). Path is relative to the theme directory.
    /// </summary>
    public static readonly AttachedProperty<string?> AttractModeSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "AttractModeSound");

    public static string? GetAttractModeSound(AvaloniaObject element) =>
        element.GetValue(AttractModeSoundProperty);

    public static void SetAttractModeSound(AvaloniaObject element, string? value) =>
        element.SetValue(AttractModeSoundProperty, value);

    /// <summary>
    /// Gets or sets the absolute base directory of the currently active theme.
    /// Example: "/home/user/Retromind/Themes/Arcade".
    /// This is set by the host (ThemeLoader) when a theme is loaded.
    /// </summary>
    public static string? ThemeBasePath { get; set; }

    /// <summary>
    /// Combines the current ThemeBasePath with a theme-relative path.
    /// If ThemeBasePath is not set or the relative path is empty, returns null.
    /// This method uses Path.Combine semantics and is intended for use in converters
    /// or code-behind, not directly from XAML.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the theme directory, e.g. "Images/cabinet.png" or "sounds/navigate.wav".
    /// </param>
    public static string? GetThemeFilePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(ThemeBasePath))
            return null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        // Use System.IO.Path.Combine to keep it portable across platforms.
        return System.IO.Path.Combine(ThemeBasePath, relativePath);
    }

    /// <summary>
    /// Utility method for theme authors to build a portable path for images.
    /// Equivalent to GetThemeFilePath("Images/" + fileName).
    /// </summary>
    /// <param name="fileName">File name inside the "Images" subfolder, e.g. "cabinet.png".</param>
    public static string? GetThemeImagePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return GetThemeFilePath(System.IO.Path.Combine("Images", fileName));
    }
    
    /// <summary>
    /// Eckradius für den Video-Overlay-Rahmen. Standard: 12 (passt zu Tiles).
    /// </summary>
    public static readonly AttachedProperty<CornerRadius> VideoCornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, CornerRadius>(
            "VideoCornerRadius",
            defaultValue: new CornerRadius(12));

    public static CornerRadius GetVideoCornerRadius(AvaloniaObject element) =>
        element.GetValue(VideoCornerRadiusProperty);

    public static void SetVideoCornerRadius(AvaloniaObject element, CornerRadius value) =>
        element.SetValue(VideoCornerRadiusProperty, value);

    /// <summary>
    /// Rahmenstärke für den Video-Overlay-Rand. Standard: 0 (kein Rahmen).
    /// </summary>
    public static readonly AttachedProperty<Thickness> VideoBorderThicknessProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "VideoBorderThickness",
            defaultValue: new Thickness(0));

    public static Thickness GetVideoBorderThickness(AvaloniaObject element) =>
        element.GetValue(VideoBorderThicknessProperty);

    public static void SetVideoBorderThickness(AvaloniaObject element, Thickness value) =>
        element.SetValue(VideoBorderThicknessProperty, value);

    /// <summary>
    /// Internal helper: stores the original margin of a visual slot so that
    /// repeated enter-animations can always return to the same base position.
    /// </summary>
    public static readonly AttachedProperty<Thickness> VisualBaseMarginProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "VisualBaseMargin",
            defaultValue: new Thickness(0));

    public static Thickness GetVisualBaseMargin(AvaloniaObject element) =>
        element.GetValue(VisualBaseMarginProperty);

    public static void SetVisualBaseMargin(AvaloniaObject element, Thickness value) =>
        element.SetValue(VisualBaseMarginProperty, value);

    /// <summary>
    /// Internal helper: indicates whether VisualBaseMargin has been initialized
    /// for a given control.
    /// </summary>
    public static readonly AttachedProperty<bool> VisualBaseMarginInitializedProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "VisualBaseMarginInitialized",
            defaultValue: false);

    public static bool GetVisualBaseMarginInitialized(AvaloniaObject element) =>
        element.GetValue(VisualBaseMarginInitializedProperty);

    public static void SetVisualBaseMarginInitialized(AvaloniaObject element, bool value) =>
        element.SetValue(VisualBaseMarginInitializedProperty, value);
    
    /// <summary>
    /// Steuert, wie das Video im Slot skaliert wird.
    /// "Fill" (Standard), "Uniform", "UniformToFill".
    /// Das Host-Control interpretiert diesen Wert.
    /// </summary>
    public static readonly AttachedProperty<string> VideoStretchModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "VideoStretchMode",
            defaultValue: "Fill");

    public static string GetVideoStretchMode(AvaloniaObject element) =>
        element.GetValue(VideoStretchModeProperty);

    public static void SetVideoStretchMode(AvaloniaObject element, string value) =>
        element.SetValue(VideoStretchModeProperty, value);
    
    /// <summary>
    /// Relativer Pfad zu einem optionalen Hintergrundvideo für das Theme
    /// (z.B. "Video/bkg_anim.mp4"). Wird relativ zum Theme-Basisordner aufgelöst.
    /// </summary>
    public static readonly AttachedProperty<string?> SecondaryBackgroundVideoPathProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "SecondaryBackgroundVideoPath");

    public static string? GetSecondaryBackgroundVideoPath(AvaloniaObject element) =>
        element.GetValue(SecondaryBackgroundVideoPathProperty);

    public static void SetSecondaryBackgroundVideoPath(AvaloniaObject element, string? value) =>
        element.SetValue(SecondaryBackgroundVideoPathProperty, value);

}