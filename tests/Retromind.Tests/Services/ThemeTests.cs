using Avalonia.Controls;
using System.Xml.Linq;
using Retromind.Extensions;
using Retromind.Helpers;
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
