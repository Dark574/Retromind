using Avalonia.Controls;
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
