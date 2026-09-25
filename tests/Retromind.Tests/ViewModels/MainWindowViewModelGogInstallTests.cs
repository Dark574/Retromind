using Retromind.Models;
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
            installedDlcsToReinstall: CreateInstalledDlcs());

        Assert.True(viewModel.ShowInstalledDlcReinstallNotice);
        Assert.Contains("2", viewModel.InstalledDlcReinstallNoticeText, StringComparison.Ordinal);
        Assert.True(viewModel.IsUpdate);
        Assert.False(viewModel.ShowCleanInstallOption);
        Assert.False(viewModel.ShowDlcReinstallSelection);
        Assert.False(viewModel.CleanInstall);

        viewModel.ClearInstalledDlcSelectionCommand.Execute(null);
        viewModel.CleanInstall = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        Assert.False(viewModel.Result!.CleanInstall);
        Assert.Equal(["1", "2"], viewModel.Result.DlcProductIdsToReinstall);
    }

    [Fact]
    public void InstallDialog_CleanReinstallUsesSelectedDlcs()
    {
        var viewModel = new GogInstallDialogViewModel(
            "Test Game",
            "/tmp/test-game",
            availableRunnerConfigs: null,
            isUpdate: false,
            installedDlcsToReinstall: CreateInstalledDlcs());

        Assert.False(viewModel.IsUpdate);
        Assert.True(viewModel.ShowCleanInstallOption);
        Assert.True(viewModel.CleanInstall);
        Assert.True(viewModel.ShowDlcReinstallSelection);
        Assert.False(viewModel.ShowInstalledDlcReinstallNotice);

        viewModel.InstalledDlcOptions[1].IsSelected = false;

        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        Assert.True(viewModel.Result!.CleanInstall);
        Assert.Equal(["1"], viewModel.Result.DlcProductIdsToReinstall);
    }

    [Fact]
    public void InstallDialog_InPlaceReinstallForcesAllInstalledDlcs()
    {
        var viewModel = new GogInstallDialogViewModel(
            "Test Game",
            "/tmp/test-game",
            availableRunnerConfigs: null,
            isUpdate: false,
            installedDlcsToReinstall: CreateInstalledDlcs());
        viewModel.ClearInstalledDlcSelectionCommand.Execute(null);
        viewModel.CleanInstall = false;

        Assert.False(viewModel.ShowDlcReinstallSelection);
        Assert.True(viewModel.ShowInstalledDlcReinstallNotice);

        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        Assert.False(viewModel.Result!.CleanInstall);
        Assert.Equal(["1", "2"], viewModel.Result.DlcProductIdsToReinstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DlcInstallRequest_InheritsInstallerCleanupChoice(bool deleteStagingAfterSuccess)
    {
        var request = MainWindowViewModel.CreateGogDlcInstallRequest(
            "/tmp/test-game",
            GogInstallPlatform.Linux,
            GogInstallDialogViewModel.WindowsInstallerPreference.AutoPrefer64,
            deleteStagingAfterSuccess);

        Assert.Equal(deleteStagingAfterSuccess, request.DeleteStagingAfterSuccess);
    }

    private static GogDlcInstallationState[] CreateInstalledDlcs() =>
    [
        new() { ProductId = "1", Title = "First DLC" },
        new() { ProductId = "2", Title = "Second DLC" }
    ];
}
