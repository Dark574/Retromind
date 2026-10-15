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
    private Func<Window, Task<bool>>? _gogLink;
    private Func<Window, Task<bool>>? _epicLink;
    private Func<Window, Task<bool>>? _gogInstallOrReinstall;
    private Func<Window, Task>? _gogCheckUpdates;
    private Func<Window, Task<bool>>? _gogUpdate;
    private Func<Window, Task<bool>>? _gogUninstall;
    private Func<Window, Task>? _gogManageDlcs;
    private bool _isStoreLinkOperationRunning;
    private bool _isGogOperationRunning;

    public IAsyncRelayCommand<Window?> GogLinkCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicLinkCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogInstallOrReinstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogCheckUpdatesCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogUpdateCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogUninstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> GogManageDlcsCommand { get; private set; } = null!;

    public bool IsGogManagementVisible =>
        GogMediaItemStateHelper.TryGetGameId(_originalItem) != null;

    public bool IsStoreLinkingVisible
    {
        get
        {
            var providerId = StoreProviderBadgeHelper.GetProviderId(_originalItem);
            return providerId == null ||
                   string.Equals(providerId, StoreProviderBadgeHelper.GogProviderId, StringComparison.Ordinal) ||
                   string.Equals(providerId, StoreProviderBadgeHelper.EpicProviderId, StringComparison.Ordinal);
        }
    }

    public bool ShowGogLinkAction =>
        IsStoreLinkingVisible &&
        !string.Equals(
            StoreProviderBadgeHelper.GetProviderId(_originalItem),
            StoreProviderBadgeHelper.GogProviderId,
            StringComparison.Ordinal);

    public bool ShowEpicLinkAction
    {
        get
        {
            var providerId = StoreProviderBadgeHelper.GetProviderId(_originalItem);
            return IsStoreLinkingVisible &&
                   (!string.Equals(
                        providerId,
                        StoreProviderBadgeHelper.EpicProviderId,
                        StringComparison.Ordinal) ||
                    !StoreGameIdentityHelper.HasStoredIdentity(_originalItem));
        }
    }

    public bool ShowGogUpdateAction =>
        GogMediaItemStateHelper.HasUpdateAvailable(_originalItem);

    public bool ShowGogCheckUpdatesAction =>
        GogMediaItemStateHelper.CanCheckUpdates(_originalItem);

    public bool ShowGogUninstallAction =>
        GogMediaItemStateHelper.CanUninstall(_originalItem);

    public bool ShowGogInstalledVersion =>
        GogMediaItemStateHelper.TryGetInstalledVersion(_originalItem) != null;

    public bool IsStoreLinkOperationRunning
    {
        get => _isStoreLinkOperationRunning;
        private set
        {
            if (!SetProperty(ref _isStoreLinkOperationRunning, value))
                return;

            GogLinkCommand.NotifyCanExecuteChanged();
            EpicLinkCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEditorBusy));
        }
    }

    public bool IsGogOperationRunning
    {
        get => _isGogOperationRunning;
        private set
        {
            if (!SetProperty(ref _isGogOperationRunning, value))
                return;

            GogInstallOrReinstallCommand.NotifyCanExecuteChanged();
            GogCheckUpdatesCommand.NotifyCanExecuteChanged();
            GogUpdateCommand.NotifyCanExecuteChanged();
            GogUninstallCommand.NotifyCanExecuteChanged();
            GogManageDlcsCommand.NotifyCanExecuteChanged();
            GogLinkCommand.NotifyCanExecuteChanged();
            EpicLinkCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEditorBusy));
        }
    }

    public string StoreLinkTitle => T("Store.Link.Title", "Store link");
    public string StoreLinkStatusText
    {
        get
        {
            var providerId = StoreProviderBadgeHelper.GetProviderId(_originalItem);
            if (string.Equals(providerId, StoreProviderBadgeHelper.GogProviderId, StringComparison.Ordinal))
                return T("Store.Link.LinkedGog", "Linked to GOG");
            if (string.Equals(providerId, StoreProviderBadgeHelper.EpicProviderId, StringComparison.Ordinal))
            {
                return StoreGameIdentityHelper.HasStoredIdentity(_originalItem)
                    ? T("Store.Link.LinkedEpic", "Linked to Epic Games")
                    : T("Store.Link.LegacyEpic", "Legacy Heroic/Epic entry");
            }

            return T("Store.Link.NotLinked", "Not linked to a store");
        }
    }

    public string StoreLinkHint => StoreProviderBadgeHelper.GetProviderId(_originalItem) == null
        ? T(
            "Store.Link.Hint",
            "Link this item to a game from your GOG or Epic Games library. Media and metadata are preserved.")
        : T(
            "Store.Link.ChangeHint",
            "Change the store association while preserving this item, its media and metadata. Retromind-managed installations must be uninstalled first.");

    public string GogLinkText => string.Equals(
        StoreProviderBadgeHelper.GetProviderId(_originalItem),
        StoreProviderBadgeHelper.EpicProviderId,
        StringComparison.Ordinal)
        ? T("Gog.Link.SwitchButton", "Switch to GOG...")
        : T("Gog.Link.Button", "Link to GOG game...");

    public string EpicLinkText => StoreProviderBadgeHelper.GetProviderId(_originalItem) == null ||
                                  !StoreGameIdentityHelper.HasStoredIdentity(_originalItem)
        ? T("Epic.Link.Button", "Link to Epic Games title...")
        : T("Epic.Link.SwitchButton", "Switch to Epic Games...");
    public string GogManagementTitle => "GOG";

    public string GogManagementHint => T(
        "EditMedia.GogManagementHint",
        "Store actions are applied immediately. Successfully changed launch settings are reloaded in this editor.");

    public string GogInstallOrReinstallText => GogMediaItemStateHelper.IsInstalled(_originalItem)
        ? Strings.Button_ReinstallOrSwitchVersion
        : Strings.Button_Install;

    public string GogUpdateText => Strings.Button_Update;
    public string GogCheckUpdatesText => T("Gog.Update.CheckNow", "Check for GOG updates");
    public string GogUninstallText => Strings.Gog_Uninstall_ContextMenu;
    public string GogInstalledVersionText
    {
        get
        {
            var version = GogMediaItemStateHelper.TryGetInstalledVersion(_originalItem);
            return version == null
                ? string.Empty
                : string.Format(
                    T("EditMedia.GogInstalledVersionFormat", "Installed version: {0}"),
                    version);
        }
    }
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
        Func<Window, Task<bool>>? gogLink,
        Func<Window, Task<bool>>? epicLink,
        Func<Window, Task<bool>>? installOrReinstall,
        Func<Window, Task>? checkUpdates,
        Func<Window, Task<bool>>? update,
        Func<Window, Task<bool>>? uninstall,
        Func<Window, Task>? manageDlcs)
    {
        _gogLink = gogLink;
        _epicLink = epicLink;
        _gogInstallOrReinstall = installOrReinstall;
        _gogCheckUpdates = checkUpdates;
        _gogUpdate = update;
        _gogUninstall = uninstall;
        _gogManageDlcs = manageDlcs;

        GogLinkCommand = new AsyncRelayCommand<Window?>(
            owner => RunStoreLinkAsync(owner, _gogLink),
            owner => ShowGogLinkAction && CanRunStoreLink(owner, _gogLink));
        EpicLinkCommand = new AsyncRelayCommand<Window?>(
            owner => RunStoreLinkAsync(owner, _epicLink),
            owner => ShowEpicLinkAction && CanRunStoreLink(owner, _epicLink));
        GogInstallOrReinstallCommand = new AsyncRelayCommand<Window?>(
            owner => RunGogActionAsync(owner, _gogInstallOrReinstall),
            owner => CanRunGogAction(owner, _gogInstallOrReinstall));
        GogCheckUpdatesCommand = new AsyncRelayCommand<Window?>(
            RunGogUpdateCheckAsync,
            owner => CanRunGogUpdateCheck(owner));
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

    private bool CanRunStoreLink(
        Window? owner,
        Func<Window, Task<bool>>? action) =>
        owner != null &&
        action != null &&
        IsStoreLinkingVisible &&
        !IsStoreLinkOperationRunning &&
        !IsGogOperationRunning &&
        !IsEpicOperationRunning;

    private bool CanRunGogDlcDialog(Window? owner) =>
        owner != null &&
        _gogManageDlcs != null &&
        IsGogManagementVisible &&
        !IsGogOperationRunning;

    private bool CanRunGogUpdateCheck(Window? owner) =>
        owner != null &&
        _gogCheckUpdates != null &&
        ShowGogCheckUpdatesAction &&
        !GogMediaItemStateHelper.HasAnyUpdateAvailable(_originalItem) &&
        !IsGogOperationRunning;

    private async Task RunStoreLinkAsync(
        Window? owner,
        Func<Window, Task<bool>>? action)
    {
        if (!CanRunStoreLink(owner, action) || owner == null || action == null)
            return;

        IsStoreLinkOperationRunning = true;
        try
        {
            if (await action(owner))
                ReloadLaunchConfigurationFromOriginalItem();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Store] Store linking failed: {ex.Message}");
        }
        finally
        {
            IsStoreLinkOperationRunning = false;
            RefreshStoreLinkState();
            RefreshGogManagementState();
            RefreshEpicManagementState();
        }
    }

    private async Task RunGogUpdateCheckAsync(Window? owner)
    {
        if (!CanRunGogUpdateCheck(owner) || owner == null || _gogCheckUpdates == null)
            return;

        IsGogOperationRunning = true;
        try
        {
            await _gogCheckUpdates(owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Manual update check failed: {ex.Message}");
        }
        finally
        {
            IsGogOperationRunning = false;
            RefreshGogManagementState();
        }
    }

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
        OnPropertyChanged(nameof(ShowGogCheckUpdatesAction));
        OnPropertyChanged(nameof(ShowGogUpdateAction));
        OnPropertyChanged(nameof(ShowGogUninstallAction));
        OnPropertyChanged(nameof(ShowGogInstalledVersion));
        OnPropertyChanged(nameof(GogInstalledVersionText));
        OnPropertyChanged(nameof(GogInstallOrReinstallText));
        OnPropertyChanged(nameof(GogManagementStatusText));
        GogInstallOrReinstallCommand.NotifyCanExecuteChanged();
        GogCheckUpdatesCommand.NotifyCanExecuteChanged();
        GogUpdateCommand.NotifyCanExecuteChanged();
        GogUninstallCommand.NotifyCanExecuteChanged();
        GogManageDlcsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshStoreLinkState()
    {
        OnPropertyChanged(nameof(IsStoreLinkingVisible));
        OnPropertyChanged(nameof(ShowGogLinkAction));
        OnPropertyChanged(nameof(ShowEpicLinkAction));
        OnPropertyChanged(nameof(StoreLinkStatusText));
        OnPropertyChanged(nameof(StoreLinkHint));
        OnPropertyChanged(nameof(GogLinkText));
        OnPropertyChanged(nameof(EpicLinkText));
        GogLinkCommand.NotifyCanExecuteChanged();
        EpicLinkCommand.NotifyCanExecuteChanged();
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
