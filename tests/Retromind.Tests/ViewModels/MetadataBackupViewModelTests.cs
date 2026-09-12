using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MetadataBackupViewModelTests
{
    [Fact]
    public async Task Restore_DefaultsToLibraryOnlyAndPassesSelectedModeToCoordinator()
    {
        using var temp = new TemporaryDirectory();
        var service = new MetadataBackupService(temp.RootPath);
        var backup = await service.CreateBackupAsync(
            new MetadataBackupContent("[]", "{}"),
            MetadataBackupReason.Manual);
        MetadataRestoreMode? restoredMode = null;
        bool? closeResult = null;

        var viewModel = new MetadataBackupViewModel(
            service,
            _ => Task.FromResult(backup),
            (_, mode) =>
            {
                restoredMode = mode;
                return Task.CompletedTask;
            },
            createsSafetyBackupBeforeRestore: true);
        viewModel.RequestConfirmation += _ => Task.FromResult(true);
        viewModel.RequestClose += result => closeResult = result;
        await viewModel.LoadAsync();

        Assert.Equal(MetadataRestoreMode.LibraryOnly, viewModel.SelectedRestoreMode.Value);

        viewModel.SelectedRestoreMode = viewModel.RestoreModes.Single(
            option => option.Value == MetadataRestoreMode.LibraryAndSettings);
        await viewModel.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(MetadataRestoreMode.LibraryAndSettings, restoredMode);
        Assert.True(closeResult);
    }
}
