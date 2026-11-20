using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Epic;

namespace Retromind.ViewModels;

internal sealed partial class DesktopEpicWorkflow : DesktopStoreWorkflow
{
    private readonly IStoreAuthProvider _epicStoreAuthProvider;
    private readonly IStoreLibraryProvider _epicStoreLibraryProvider;
    private readonly EpicInstallService _epicInstallService;
    private readonly EpicLaunchService _epicLaunchService;

    internal DesktopEpicWorkflow(
        DesktopStoreWorkspace workspace,
        IStoreAuthProvider authProvider,
        IStoreLibraryProvider libraryProvider,
        EpicInstallService installService,
        EpicLaunchService launchService) : base(workspace)
    {
        _epicStoreAuthProvider = authProvider;
        _epicStoreLibraryProvider = libraryProvider;
        _epicInstallService = installService;
        _epicLaunchService = launchService;
    }
}
