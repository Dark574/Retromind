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

    [Fact]
    public void InstallDialog_UpdateIsAlwaysInPlaceAndShowsDlcCount()
    {
        var viewModel = new GogInstallDialogViewModel(
            "Test Game",
            "/tmp/test-game",
            availableRunnerConfigs: null,
            isUpdate: true,
            installedDlcReinstallCount: 31);

        Assert.True(viewModel.ShowInstalledDlcReinstallNotice);
        Assert.Contains("31", viewModel.InstalledDlcReinstallNoticeText, StringComparison.Ordinal);
        Assert.True(viewModel.IsUpdate);
        Assert.False(viewModel.ShowCleanInstallOption);
        Assert.False(viewModel.CleanInstall);

        viewModel.CleanInstall = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        Assert.False(viewModel.Result!.CleanInstall);
    }

    [Fact]
    public void InstallDialog_ReinstallRetainsCleanInstallChoiceAndShowsDlcCount()
    {
        var viewModel = new GogInstallDialogViewModel(
            "Test Game",
            "/tmp/test-game",
            availableRunnerConfigs: null,
            isUpdate: false,
            installedDlcReinstallCount: 31);

        Assert.False(viewModel.IsUpdate);
        Assert.True(viewModel.ShowCleanInstallOption);
        Assert.True(viewModel.CleanInstall);
        Assert.True(viewModel.ShowInstalledDlcReinstallNotice);

        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        Assert.True(viewModel.Result!.CleanInstall);
    }
}
