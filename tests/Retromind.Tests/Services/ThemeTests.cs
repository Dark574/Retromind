using Avalonia.Controls;
using System.Xml.Linq;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class ThemeTests
{
    [Fact]
    public void Constructor_PreservesDisabledSecondaryVideoWhenBackgroundPathExists()
    {
        var theme = new Theme(
            new Border(),
            new ThemeSounds(),
            "/theme",
            secondaryBackgroundVideoPath: "Videos/background.mp4",
            secondaryVideoEnabled: false);

        Assert.False(theme.SecondaryVideoEnabled);
    }

    [Fact]
    public void Constructor_HomeSupportIsOptIn()
    {
        var classicTheme = new Theme(new Border(), new ThemeSounds(), "/theme");
        var homeTheme = new Theme(
            new Border(),
            new ThemeSounds(),
            "/theme",
            supportsHome: true);

        Assert.False(classicTheme.SupportsHome);
        Assert.True(homeTheme.SupportsHome);
    }

    [Fact]
    public void ProvidesSystemMenuHint_IsOptIn()
    {
        var hostHintTheme = new Border();
        var customHintTheme = new Border();

        ThemeProperties.SetProvidesSystemMenuHint(customHintTheme, true);

        Assert.False(ThemeProperties.GetProvidesSystemMenuHint(hostHintTheme));
        Assert.True(ThemeProperties.GetProvidesSystemMenuHint(customHintTheme));
    }

    [Theory]
    [InlineData("Arcade/theme.axaml")]
    [InlineData("ArchiveAtlas/theme.axaml")]
    [InlineData("Default/theme.axaml")]
    [InlineData("HorizontalRow/theme.axaml")]
    [InlineData("LivingRoom/theme.axaml")]
    [InlineData("Prism/theme.axaml")]
    [InlineData("System/theme.axaml")]
    [InlineData("Wheel/theme.axaml")]
    public void ShippedRootTheme_DeclaresHomeSupport(string themePath)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Themes", themePath);
        var root = XDocument.Load(filePath).Root;
        var supportsHome = root?.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, "ThemeProperties.SupportsHome", StringComparison.Ordinal));

        Assert.Equal("True", supportsHome?.Value);
    }

    [Fact]
    public void DefaultTheme_DoesNotRetainPreviousVideoFrameDuringFade()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Themes", "Default", "theme.axaml");
        var root = XDocument.Load(filePath).Root;
        var retainPreviousFrame = root?.Attributes().FirstOrDefault(attribute =>
            string.Equals(
                attribute.Name.LocalName,
                "ThemeProperties.VideoRetainPreviousFrameDuringFade",
                StringComparison.Ordinal));

        Assert.Equal("False", retainPreviousFrame?.Value);
    }

    [Fact]
    public void DefaultTheme_CoverEntryWaitsForCachedArtwork()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Themes", "Default", "theme.axaml");
        var root = XDocument.Load(filePath).Root;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var coverPanel = root?
            .Descendants()
            .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == "CoverPanel");
        var coverImages = coverPanel?
            .Descendants()
            .Where(element => element.Name.LocalName == "Image")
            .ToList();
        var deferredLargeArtwork = root?
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "CrossfadeImage" &&
                element.Attribute("DecodeWidth")?.Value is "1280" or "1920")
            .ToList();

        Assert.NotNull(root);
        Assert.NotNull(coverPanel);
        Assert.Equal(
            "True",
            coverPanel.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.WaitForAsyncImageBeforeEnter")?.Value);
        Assert.Equal(
            "320",
            root.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.FadeDurationMs")?.Value);
        Assert.Equal(
            "1100",
            root.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.MoveDurationMs")?.Value);
        Assert.Equal(
            "-220",
            root.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.PrimaryVisualEnterOffsetX")?.Value);
        Assert.Equal(
            "True",
            coverPanel.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.UseCompositorEnterAnimation")?.Value);
        Assert.Equal(
            "True",
            coverPanel.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "ThemeProperties.WaitForPreviewMediaBeforeEnter")?.Value);
        Assert.NotNull(coverImages);
        Assert.Equal(2, coverImages.Count);
        Assert.All(
            coverImages,
            image =>
            {
                Assert.Equal(
                    "256",
                    image.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "AsyncImageHelper.DecodeWidth")?.Value);
                Assert.DoesNotContain(
                    image.Attributes(),
                    attribute => attribute.Name.LocalName == "AsyncImageHelper.DisableCache");
            });
        Assert.NotNull(deferredLargeArtwork);
        Assert.Equal(4, deferredLargeArtwork.Count);
        Assert.All(
            deferredLargeArtwork,
            image => Assert.Equal(
                "True",
                image.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName ==
                    "ThemeProperties.ContributesToPreviewMediaReadiness")?.Value));
        Assert.All(
            deferredLargeArtwork,
            image => Assert.Equal("220", image.Attribute("LoadDelayMs")?.Value));
    }

    [Fact]
    public void ArcadeTheme_QualifiesAsyncImageBindingWithRetromindAssembly()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Themes", "Arcade", "theme.axaml");
        var root = XDocument.Load(filePath).Root;
        var helpersNamespace = root?.GetNamespaceOfPrefix("helpers");
        var isLoadedBinding = root?
            .Descendants()
            .Attributes()
            .FirstOrDefault(attribute => attribute.Value.Contains(
                "(helpers:AsyncImageHelper.IsLoaded)",
                StringComparison.Ordinal));

        Assert.Equal(
            "clr-namespace:Retromind.Helpers;assembly=Retromind",
            helpersNamespace?.NamespaceName);
        Assert.NotNull(isLoadedBinding);
    }

    [Fact]
    public void PrismTheme_CarouselsBufferItemsBeyondTheirViewports()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Themes", "Prism", "theme.axaml");
        var root = XDocument.Load(filePath).Root;
        var carouselPanels = root?
            .Descendants()
            .Where(element => element.Name.LocalName == "VirtualizingStackPanel")
            .ToList();

        Assert.NotNull(carouselPanels);
        Assert.Equal(2, carouselPanels.Count);
        Assert.All(
            carouselPanels,
            panel => Assert.Equal("0.5", panel.Attribute("CacheLength")?.Value));
    }

    [Fact]
    public void VideoPreview_DoesNotRetainPreviousFrameByDefault()
    {
        Assert.False(ThemeProperties.VideoRetainPreviousFrameDuringFadeProperty.GetDefaultValue(typeof(Border)));
        Assert.False(CrossfadeVideoSurfaceControl.RetainPreviousSurfaceDuringFadeProperty
            .GetDefaultValue(typeof(CrossfadeVideoSurfaceControl)));
    }

    [Fact]
    public void GetThemeFilePath_UsesTheSuppliedViewScope()
    {
        var arcadeView = new Border();
        var systemView = new Border();
        ThemeProperties.SetThemeBasePath(arcadeView, "/themes/Arcade");
        ThemeProperties.SetThemeBasePath(systemView, "/themes/System/C64");

        Assert.Equal(
            Path.Combine("/themes/Arcade", "Images/cabinet.png"),
            ThemeProperties.GetThemeFilePath("Images/cabinet.png", arcadeView));
        Assert.Equal(
            Path.Combine("/themes/System/C64", "Images/frame.png"),
            ThemeProperties.GetThemeFilePath("Images/frame.png", systemView));
    }

    [Theory]
    [InlineData("Fonts/ThemeFont.ttf")]
    [InlineData("ThemeFont.otf")]
    [InlineData("ThemeFont.ttc")]
    public void LooksLikeFontPath_AcceptsSupportedThemeFontPaths(string value)
    {
        Assert.True(ThemeFontFamilyConverter.LooksLikeFontPath(value));
    }

    [Theory]
    [InlineData("Arial")]
    [InlineData("IBM Plex Sans")]
    [InlineData("font.txt")]
    public void LooksLikeFontPath_RejectsSystemFontNamesAndUnsupportedFiles(string value)
    {
        Assert.False(ThemeFontFamilyConverter.LooksLikeFontPath(value));
    }
}
