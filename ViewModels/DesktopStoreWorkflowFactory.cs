using Retromind.Models.Stores;
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
    EpicLaunchService epicLaunchService)
{
    internal (DesktopGogWorkflow Gog, DesktopEpicWorkflow Epic) Create(DesktopStoreWorkspace workspace) =>
        (
            new DesktopGogWorkflow(
                workspace,
                providers.GetRequiredCapability<IStoreAuthProvider>(StoreProviderIds.Gog),
                providers.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Gog),
                gogInstallService,
                gogLaunchDetectionService,
                gogInstallerWorkflowService),
            new DesktopEpicWorkflow(
                workspace,
                providers.GetRequiredCapability<IStoreAuthProvider>(StoreProviderIds.Epic),
                providers.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Epic),
                epicInstallService,
                epicLaunchService)
        );
}
