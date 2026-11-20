using System.Threading.Tasks;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow : DesktopStoreWorkflow
{
    private readonly IStoreAuthProvider _gogStoreAuthProvider;
    private readonly IStoreLibraryProvider _gogStoreLibraryProvider;
    private readonly GogInstallService _gogInstallService;
    private readonly GogLaunchDetectionService _gogLaunchDetectionService;
    private readonly GogInstallerWorkflowService _gogInstallerWorkflowService;
    private readonly GogUpdateCheckService _gogUpdateChecks;

    internal DesktopGogWorkflow(
        DesktopStoreWorkspace workspace,
        IStoreAuthProvider authProvider,
        IStoreLibraryProvider libraryProvider,
        GogInstallService installService,
        GogLaunchDetectionService launchDetectionService,
        GogInstallerWorkflowService installerWorkflowService) : base(workspace)
    {
        _gogStoreAuthProvider = authProvider;
        _gogStoreLibraryProvider = libraryProvider;
        _gogInstallService = installService;
        _gogLaunchDetectionService = launchDetectionService;
        _gogInstallerWorkflowService = installerWorkflowService;
        _gogUpdateChecks = new GogUpdateCheckService(authProvider, installService);
    }

    internal Task StopBackgroundChecksAsync() => _gogUpdateChecks.StopBackgroundChecksAsync();
}
