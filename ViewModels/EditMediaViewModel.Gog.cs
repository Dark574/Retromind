using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private Func<Window, Task<bool>>? _gogInstallOrReinstall;
    private Func<Window, Task<bool>>? _gogUpdate;
    private Func<Window, Task<bool>>? _gogUninstall;
    private Func<Window, Task>? _gogManageDlcs;
    private bool _isGogOperationRunning;

    public IAsyncRelayCommand<Window?> GogInstallOrReinstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogUpdateCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogUninstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogManageDlcsCommand { get; private set; } = null!;

    public bool IsGogManagementVisible =>
        GogMediaItemStateHelper.TryGetGameId(_originalItem) != null;

    public bool ShowGogUpdateAction =>
        GogMediaItemStateHelper.HasUpdateAvailable(_originalItem);

    public bool ShowGogUninstallAction =>
        GogMediaItemStateHelper.CanUninstall(_originalItem);

    public bool IsGogOperationRunning
    {
        get => _isGogOperationRunning;
        private set
        {
            if (!SetProperty(ref _isGogOperationRunning, value))
                return;

            GogInstallOrReinstallCommand.NotifyCanExecuteChanged();
            GogUpdateCommand.NotifyCanExecuteChanged();
            GogUninstallCommand.NotifyCanExecuteChanged();
            GogManageDlcsCommand.NotifyCanExecuteChanged();
        }
    }

    public string GogManagementTitle => "GOG";

    public string GogManagementHint => T(
        "EditMedia.GogManagementHint",
        "Store actions are applied immediately. Successfully changed launch settings are reloaded in this editor.");

    public string GogInstallOrReinstallText => GogMediaItemStateHelper.IsInstalled(_originalItem)
        ? Strings.Button_ReinstallOrSwitchVersion
        : Strings.Button_Install;

    public string GogUpdateText => Strings.Button_Update;
    public string GogUninstallText => Strings.Gog_Uninstall_ContextMenu;
    public string GogDlcTitle => T("EditMedia.GogDlcTitle", "DLCs");
    public string GogDlcHint => T(
        "EditMedia.GogDlcHint",
        "Show owned DLCs and their available offline installers.");
    public string GogManageDlcsText => T("EditMedia.GogManageDlcs", "Manage DLCs...");

    public string GogManagementStatusText
    {
        get
        {
            if (ShowGogUpdateAction)
            {
                return T(
                    "EditMedia.GogStatusUpdateAvailable",
                    "Installed – update available");
            }

            return GogMediaItemStateHelper.IsInstalled(_originalItem)
                ? T("EditMedia.GogStatusInstalled", "Installed")
                : T("EditMedia.GogStatusNotInstalled", "Not installed");
        }
    }

    private void InitializeGogManagement(
        Func<Window, Task<bool>>? installOrReinstall,
        Func<Window, Task<bool>>? update,
        Func<Window, Task<bool>>? uninstall,
        Func<Window, Task>? manageDlcs)
    {
        _gogInstallOrReinstall = installOrReinstall;
        _gogUpdate = update;
        _gogUninstall = uninstall;
        _gogManageDlcs = manageDlcs;

        GogInstallOrReinstallCommand = new AsyncRelayCommand<Window?>(
            owner => RunGogActionAsync(owner, _gogInstallOrReinstall),
            owner => CanRunGogAction(owner, _gogInstallOrReinstall));
        GogUpdateCommand = new AsyncRelayCommand<Window?>(
            owner => RunGogActionAsync(owner, _gogUpdate),
            owner => ShowGogUpdateAction && CanRunGogAction(owner, _gogUpdate));
        GogUninstallCommand = new AsyncRelayCommand<Window?>(
            owner => RunGogActionAsync(owner, _gogUninstall),
            owner => ShowGogUninstallAction && CanRunGogAction(owner, _gogUninstall));
        GogManageDlcsCommand = new AsyncRelayCommand<Window?>(
            RunGogDlcDialogAsync,
            owner => CanRunGogDlcDialog(owner));
    }

    private bool CanRunGogAction(
        Window? owner,
        Func<Window, Task<bool>>? action) =>
        owner != null &&
        action != null &&
        IsGogManagementVisible &&
        !IsGogOperationRunning;

    private bool CanRunGogDlcDialog(Window? owner) =>
        owner != null &&
        _gogManageDlcs != null &&
        IsGogManagementVisible &&
        !IsGogOperationRunning;

    private async Task RunGogDlcDialogAsync(Window? owner)
    {
        if (!CanRunGogDlcDialog(owner) || owner == null || _gogManageDlcs == null)
            return;

        IsGogOperationRunning = true;
        try
        {
            await _gogManageDlcs(owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] DLC dialog failed: {ex.Message}");
        }
        finally
        {
            IsGogOperationRunning = false;
        }
    }

    private async Task RunGogActionAsync(
        Window? owner,
        Func<Window, Task<bool>>? action)
    {
        if (!CanRunGogAction(owner, action) || owner == null || action == null)
            return;

        IsGogOperationRunning = true;
        try
        {
            if (await action(owner))
                ReloadLaunchConfigurationFromOriginalItem();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Media-editor action failed: {ex.Message}");
        }
        finally
        {
            IsGogOperationRunning = false;
            RefreshGogManagementState();
        }
    }

    private void RefreshGogManagementState()
    {
        OnPropertyChanged(nameof(IsGogManagementVisible));
        OnPropertyChanged(nameof(ShowGogUpdateAction));
        OnPropertyChanged(nameof(ShowGogUninstallAction));
        OnPropertyChanged(nameof(GogInstallOrReinstallText));
        OnPropertyChanged(nameof(GogManagementStatusText));
        GogInstallOrReinstallCommand.NotifyCanExecuteChanged();
        GogUpdateCommand.NotifyCanExecuteChanged();
        GogUninstallCommand.NotifyCanExecuteChanged();
        GogManageDlcsCommand.NotifyCanExecuteChanged();
    }

    private void ReloadLaunchConfigurationFromOriginalItem()
    {
        _editedFiles.Clear();
        _editedFiles.AddRange(CloneFiles(_originalItem.Files));

        MediaType = _originalItem.MediaType;
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        WorkingDirectory = _originalItem.WorkingDirectory ?? string.Empty;
        XdgConfigPath = _originalItem.XdgConfigPath ?? string.Empty;
        XdgDataPath = _originalItem.XdgDataPath ?? string.Empty;
        XdgCachePath = _originalItem.XdgCachePath ?? string.Empty;
        XdgStatePath = _originalItem.XdgStatePath ?? string.Empty;
        XdgBasePath = _originalItem.XdgBasePath ?? string.Empty;
        PrefixPath = _originalItem.PrefixPath ?? string.Empty;

        InitializeEmulators(_settings);
        InitializeRunnerVersions(_settings);
        InitializeNativeWrapperUiFromItem();
        RefreshInheritedWrappers();
        InitializeEnvironmentOverridesFromItem();

        // Profile selection may normalize trivial arguments. The store-generated
        // value is authoritative after an install or uninstall operation.
        LauncherArgs = _originalItem.LauncherArgs ?? string.Empty;

        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(HasLaunchFiles));
        OnPropertyChanged(nameof(PreviewText));
        RemoveLaunchFilesCommand.NotifyCanExecuteChanged();
        CopyPreviewCommand.NotifyCanExecuteChanged();
        RefreshRetroAchievementsIdentificationStatus();
    }
}
