using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class ThemePathResolverTests
{
    [Fact]
    public void TryResolveThemeAssetPath_AcceptsExistingRelativeFile()
    {
        using var temp = new TemporaryDirectory();
        var themeDirectory = temp.CreateDirectory("Themes", "Arcade");
        var assetPath = temp.CreateFile("Themes/Arcade/Images/frame.png");

        var resolved = ThemePathResolver.TryResolveThemeAssetPath(
            themeDirectory,
            "Images/frame.png",
            out var fullPath);

        Assert.True(resolved);
        Assert.Equal(assetPath, fullPath);
    }

    [Fact]
    public void TryResolveThemeAssetPath_RejectsTraversalAndAbsolutePaths()
    {
        using var temp = new TemporaryDirectory();
        var themeDirectory = temp.CreateDirectory("Themes", "Arcade");
        var outsidePath = temp.CreateFile("outside.png");

        Assert.False(ThemePathResolver.TryResolveThemeAssetPath(
            themeDirectory,
            "../../outside.png",
            out _));
        Assert.False(ThemePathResolver.TryResolveThemeAssetPath(
            themeDirectory,
            outsidePath,
            out _));
    }

    [Fact]
    public void TryResolveThemeAssetPath_RejectsSymbolicLinkInPath()
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var themeDirectory = temp.CreateDirectory("Themes", "Arcade");
        var externalImages = external.CreateDirectory("Images");
        external.CreateFile("Images/frame.png");
        Directory.CreateSymbolicLink(
            Path.Combine(themeDirectory, "Images"),
            externalImages);

        Assert.False(ThemePathResolver.TryResolveThemeAssetPath(
            themeDirectory,
            "Images/frame.png",
            out _));
    }

    [Fact]
    public void TryResolveThemeFilePath_AllowsOnlyRegularFilesBelowThemesRoot()
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var themesRoot = temp.CreateDirectory("Themes");
        var validTheme = temp.CreateFile("Themes/Arcade/theme.axaml");
        var externalTheme = external.CreateFile("theme.axaml");
        var linkedTheme = Path.Combine(themesRoot, "linked.axaml");
        File.CreateSymbolicLink(linkedTheme, externalTheme);

        Assert.True(ThemePathResolver.TryResolveThemeFilePath(
            themesRoot,
            "Arcade/theme.axaml",
            out var relativeResult));
        Assert.Equal(validTheme, relativeResult);
        Assert.True(ThemePathResolver.TryResolveThemeFilePath(
            themesRoot,
            validTheme,
            out var absoluteResult));
        Assert.Equal(validTheme, absoluteResult);
        Assert.False(ThemePathResolver.TryResolveThemeFilePath(
            themesRoot,
            externalTheme,
            out _));
        Assert.False(ThemePathResolver.TryResolveThemeFilePath(
            themesRoot,
            linkedTheme,
            out _));
    }
}
