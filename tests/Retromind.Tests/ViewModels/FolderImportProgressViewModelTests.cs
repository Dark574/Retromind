using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class FolderImportProgressViewModelTests
{
    [Fact]
    public void CancelCommand_CancelsTokenAndKeepsLibrarySafetyMessage()
    {
        using var viewModel = new FolderImportProgressViewModel();

        viewModel.CancelCommand.Execute(null);

        Assert.True(viewModel.Token.IsCancellationRequested);
        Assert.True(viewModel.IsCancelling);
        Assert.NotEmpty(viewModel.DetailText);
    }

    [Fact]
    public void Report_UpdatesVisibleScanCounts()
    {
        using var viewModel = new FolderImportProgressViewModel();

        viewModel.Report(new FolderImportProgress(
            FolderImportStage.ScanningFiles,
            FilesScanned: 1200,
            MatchingFileCount: 75));

        Assert.NotEmpty(viewModel.DetailText);
        Assert.Contains("75", viewModel.DetailText, StringComparison.Ordinal);
    }
}
