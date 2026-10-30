using Retromind.Services.Stores.Epic;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelEpicUninstallMessageTests
{
    [Theory]
    [InlineData("[LFS Utils] ERROR: Removing game directory failed with OSError(39, 'Directory not empty')", true)]
    [InlineData("[LFS Utils] ERROR: Removing game directory failed with OSError(66, 'Directory not empty')", true)]
    [InlineData("[LFS Utils] ERROR: Removing game directory failed with PermissionError(13, 'Permission denied')", false)]
    [InlineData("[cli] ERROR: Uninstall failed: Directory not empty", false)]
    [InlineData("[cli] INFO: Game has been uninstalled.", false)]
    [InlineData("", false)]
    public void NonemptyDirectoryDetectionIsRestrictedToSpecificLegendaryMessage(string line, bool expected)
    {
        Assert.Equal(expected, MainWindowViewModel.IsLegendaryNonemptyDirectoryMessage(line));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedFolderCleanupExplainsEarlierBackendMessageEvenWhenPrefixWasPreserved(bool prefixPreserved)
    {
        var notice = MainWindowViewModel.GetEpicUninstallDirectoryCleanupNotice(
            new EpicUninstallResult(prefixPreserved, null), nonemptyDirectoryReported: true);

        Assert.NotNull(notice);
        Assert.Contains("Legendary", notice);
        Assert.Contains("Retromind", notice);
    }

    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(true, "/games/retained", false, false)]
    [InlineData(true, null, true, false)]
    [InlineData(true, null, false, true)]
    public void NoResolvedNoticeForOtherMessagesRetainedFilesOrUnsuccessfulCleanup(
        bool reported, string? remainingPath, bool failed, bool cancelled)
    {
        Assert.Null(MainWindowViewModel.GetEpicUninstallDirectoryCleanupNotice(
            new EpicUninstallResult(false, remainingPath, failed, cancelled), reported));
    }
}
