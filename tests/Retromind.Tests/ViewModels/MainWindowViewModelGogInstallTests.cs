using Retromind.Services.Stores.Gog;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelGogInstallTests
{
    [Fact]
    public void Dialog_DefaultsShortcutsToDisabled()
    {
        var viewModel = new GogInstallDialogViewModel(
            "Test Game",
            "/tmp/test-game",
            availableRunnerConfigs: null,
            availablePlatforms: [GogInstallPlatform.Windows],
            preferredPlatform: GogInstallPlatform.Windows);

        Assert.False(viewModel.CreateDesktopShortcut);
        Assert.False(viewModel.CreateStartMenuShortcuts);
    }

    [Theory]
    [InlineData(false, false, "/nodesktopshorctut,/nodesktopshortcut,/nostartmenushortcut")]
    [InlineData(false, true, "/nodesktopshorctut,/nodesktopshortcut")]
    [InlineData(true, false, "/nostartmenushortcut")]
    [InlineData(true, true, "")]
    public void BuildWindowsShortcutArguments_ReflectSelections(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts,
        string expectedArguments)
    {
        var arguments = MainWindowViewModel.BuildWindowsShortcutArguments(
            createDesktopShortcut,
            createStartMenuShortcuts);
        var expected = expectedArguments.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, arguments);
    }
}
