using Retromind.Models.Stores;
using Retromind.Services;
using Retromind.Services.Downloads;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public sealed class DesktopStoreWorkflowFactory(
    StoreProviderRegistry providers,
    GogInstallService gogInstallService,
    GogLaunchDetectionService gogLaunchDetectionService,
    GogInstallerWorkflowService gogInstallerWorkflowService,
    EpicInstallService epicInstallService,
    EpicLaunchService epicLaunchService,
    DownloadManagerService downloadManager,
    GogQueuedDownloadService gogDownloads,
    IErrorService errors)
{
    internal (DesktopGogWorkflow Gog, DesktopEpicWorkflow Epic, DesktopDownloadWorkflow Downloads)
        Create(DesktopStoreWorkspace workspace)
    {
        var gog = new DesktopGogWorkflow(
            workspace,
            providers.GetRequiredCapability<IStoreAuthProvider>(StoreProviderIds.Gog),
            providers.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Gog),
            gogInstallService,
            gogLaunchDetectionService,
            gogInstallerWorkflowService);
        var epic = new DesktopEpicWorkflow(
            workspace,
            providers.GetRequiredCapability<IStoreAuthProvider>(StoreProviderIds.Epic),
            providers.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Epic),
            epicInstallService,
            epicLaunchService);
        return (gog, epic, new DesktopDownloadWorkflow(workspace, gog, epic, downloadManager, gogDownloads, errors));
    }
}
