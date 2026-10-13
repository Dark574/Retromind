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
        var node = new MediaNode("New category", NodeType.Group);

        Assert.Equal(ThemeDiscovery.DefaultThemePath, node.ThemePath);
        Assert.Equal(SystemThemeDiscovery.DefaultThemeId, node.SystemPreviewThemeId);
    }

    [Fact]
    public void ResolveEffectiveThemePath_NodeWithoutLocalThemeDoesNotUseRootTheme()
    {
        using var temp = new TemporaryDirectory();
        var defaultTheme = temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Legacy category", NodeType.Group)
        {
            ThemePath = null
        };

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node,
            "Arcade/theme.axaml",
            temp.RootPath);

        Assert.Equal(Path.GetFullPath(defaultTheme), resolved);
    }

    [Fact]
    public void ResolveEffectiveThemePath_InvalidLocalThemeFallsBackToDefault()
    {
        using var temp = new TemporaryDirectory();
        var defaultTheme = temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Category", NodeType.Group)
        {
            ThemePath = "Missing/theme.axaml"
        };

        var resolved = MainWindowViewModel.ResolveEffectiveThemePath(
            node,
            "Arcade/theme.axaml",
            temp.RootPath);

        Assert.Equal(Path.GetFullPath(defaultTheme), resolved);
    }

    [Fact]
    public void ResolveEffectiveThemePath_UsesTheNodesOwnTheme()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(ThemeDiscovery.DefaultThemePath, string.Empty);
        var arcadeTheme = temp.CreateFile("Arcade/theme.axaml", string.Empty);
        var node = new MediaNode("Category", NodeType.Group)
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
}
