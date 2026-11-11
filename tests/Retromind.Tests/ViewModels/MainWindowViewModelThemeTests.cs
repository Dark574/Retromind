using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelThemeTests
{
    [Fact]
    public void NewNode_UsesDefaultNormalAndSystemThemes()
    {
        var node = new MediaNode("New category");

        Assert.Equal(ThemeDiscovery.DefaultThemePath, node.ThemePath);
        Assert.Equal("Navigator/theme.axaml", node.ThemePath);
        Assert.Equal(node.ThemePath, new AppSettings().RootBigModeThemePath);
        Assert.Equal(SystemThemeDiscovery.DefaultThemeId, node.SystemPreviewThemeId);
    }

    [Fact]
    public void ResolveEffectiveThemePath_NodeWithoutLocalThemeDoesNotUseRootTheme()
    {
        using var temp = new TemporaryDirectory();
        var defaultTheme = temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Legacy category")
        {
            ThemePath = null
        };

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node,
            "Arcade/theme.axaml",
            temp.RootPath);

        Assert.Equal(Path.GetFullPath(defaultTheme), resolved);
    }

    [Theory]
    [InlineData("Missing/theme.axaml")]
    [InlineData("../outside/theme.axaml")]
    [InlineData("")]
    public void ResolveEffectiveThemePath_InvalidLocalThemeFallsBackToDefault(string assigned)
    {
        using var temp = new TemporaryDirectory();
        var defaultTheme = temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Category")
        {
            ThemePath = assigned
        };

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node,
            "Arcade/theme.axaml",
            temp.RootPath);

        Assert.Equal(Path.GetFullPath(defaultTheme), resolved);
        Assert.Equal(assigned, node.ThemePath);
    }

    [Fact]
    public void ResolveEffectiveThemePath_UsesTheNodesOwnTheme()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        var arcadeTheme = temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Category")
        {
            ThemePath = "Arcade/theme.axaml"
        };

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node: node,
            rootThemePath: null,
            themesRoot: temp.RootPath);

        Assert.Equal(Path.GetFullPath(arcadeTheme), resolved);
    }

    [Fact]
    public void ResolveEffectiveThemePath_VirtualRootUsesItsDedicatedTheme()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        var prismTheme = temp.CreateFile("Prism/theme.axaml", string.Empty);

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node: null,
            rootThemePath: "Prism/theme.axaml",
            themesRoot: temp.RootPath);

        Assert.Equal(Path.GetFullPath(prismTheme), resolved);
    }

    [Theory]
    [InlineData("Default/theme.axaml", false)]
    [InlineData("Default/theme.axaml", true)]
    [InlineData("Cinematic/theme.axaml", false)]
    [InlineData("Cinematic/theme.axaml", true)]
    public void ResolveEffectiveThemePath_PreservesAssignedThemesWhenNavigatorIsAvailable(string assigned, bool root)
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        var expected = temp.CreateFile(assigned, string.Empty);
        var node = root ? null : new MediaNode("Existing") { ThemePath = assigned };

        Assert.Equal(Path.GetFullPath(expected), MainWindowViewModel.ResolveEffectiveThemePath(
            node, root ? assigned : "Arcade/theme.axaml", temp.RootPath));
        if (node != null) Assert.Equal(assigned, node.ThemePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveEffectiveThemePath_MissingNavigatorFallsBackToLegacyDefault(bool root)
    {
        using var temp = new TemporaryDirectory();
        var legacy = temp.CreateFile("Default/theme.axaml", string.Empty);
        var node = root ? null : new MediaNode("New category");

        Assert.Equal(Path.GetFullPath(legacy), MainWindowViewModel.ResolveEffectiveThemePath(
            node, new AppSettings().RootBigModeThemePath, temp.RootPath));
        if (node != null) Assert.Equal("Navigator/theme.axaml", node.ThemePath);
    }

    [Fact]
    public void ResolveEffectiveThemePath_NoInstalledFallbackReturnsExpectedPathWithoutRewritingAssignment()
    {
        using var temp = new TemporaryDirectory();
        var node = new MediaNode("Existing") { ThemePath = "Missing/theme.axaml" };

        Assert.Equal(temp.GetPath("Navigator/theme.axaml"), MainWindowViewModel.ResolveEffectiveThemePath(
            node, "Arcade/theme.axaml", temp.RootPath));
        Assert.Equal("Missing/theme.axaml", node.ThemePath);
        Assert.False(File.Exists(temp.GetPath("Navigator/theme.axaml")));
    }

    [Fact]
    public void Deserialize_MissingThemeFieldsUseNavigator()
    {
        Assert.Equal("Navigator/theme.axaml", JsonSerializer.Deserialize<AppSettings>("{}")!.RootBigModeThemePath);
        Assert.Equal("Navigator/theme.axaml", JsonSerializer.Deserialize<MediaNode>("{\"Name\":\"Legacy\"}")!.ThemePath);
    }

    [Theory]
    [InlineData("Default/theme.axaml")]
    [InlineData("Prism/theme.axaml")]
    [InlineData("Custom/theme.axaml")]
    [InlineData("")]
    [InlineData(null)]
    public void DeserializeAndReserialize_PreserveStoredThemeValues(string? savedTheme)
    {
        var settingsJson = JsonSerializer.Serialize(new { RootBigModeThemePath = savedTheme });
        var nodeJson = JsonSerializer.Serialize(new { Name = "Existing", ThemePath = savedTheme });
        var settings = JsonSerializer.Deserialize<AppSettings>(settingsJson)!;
        var node = JsonSerializer.Deserialize<MediaNode>(nodeJson)!;

        Assert.Equal(savedTheme, settings.RootBigModeThemePath);
        Assert.Equal(savedTheme, node.ThemePath);
        Assert.Equal(savedTheme, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.RootBigModeThemePath);
        Assert.Equal(savedTheme, JsonSerializer.Deserialize<MediaNode>(JsonSerializer.Serialize(node))!.ThemePath);
    }
}
