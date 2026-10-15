using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private Func<Window, Task<bool>>? _epicInstall;
    private Func<Window, Task>? _epicCheckUpdates;
    private Func<Window, Task<bool>>? _epicUpdate;
    private Func<Window, Task<bool>>? _epicRepair;
    private Func<Window, Task<bool>>? _epicUninstall;
    private Func<Window, Task>? _epicManageDlcs;
    private bool _isEpicOperationRunning;

    public IAsyncRelayCommand<Window?> EpicInstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicCheckUpdatesCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicUpdateCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicRepairCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicUninstallCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Window?> EpicManageDlcsCommand { get; private set; } = null!;

    public bool IsEpicManagementVisible =>
        EpicMediaItemStateHelper.TryGetGameId(_originalItem) != null;

    public bool ShowEpicInstallAction =>
        EpicMediaItemStateHelper.ShouldOfferInstall(_originalItem);

    public bool ShowEpicCheckUpdatesAction =>
        EpicMediaItemStateHelper.CanCheckUpdates(_originalItem) &&
        !EpicMediaItemStateHelper.HasUpdateAvailable(_originalItem);

    public bool ShowEpicUpdateAction =>
        EpicMediaItemStateHelper.HasUpdateAvailable(_originalItem);

    public bool ShowEpicRepairAction =>
        EpicMediaItemStateHelper.IsRetromindManagedInstallation(_originalItem);

    public bool ShowEpicUninstallAction =>
        EpicMediaItemStateHelper.CanUninstall(_originalItem);

    public bool ShowEpicDlcAction => ShowEpicRepairAction;

    public bool ShowEpicInstalledVersion =>
        EpicMediaItemStateHelper.TryGetInstalledVersion(_originalItem) != null;

    public bool IsEpicOperationRunning
    {
        get => _isEpicOperationRunning;
        private set
        {
            if (!SetProperty(ref _isEpicOperationRunning, value))
                return;

            EpicInstallCommand.NotifyCanExecuteChanged();
            EpicCheckUpdatesCommand.NotifyCanExecuteChanged();
            EpicUpdateCommand.NotifyCanExecuteChanged();
            EpicRepairCommand.NotifyCanExecuteChanged();
            EpicUninstallCommand.NotifyCanExecuteChanged();
            EpicManageDlcsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEditorBusy));
        }
    }

    public string EpicManagementTitle => "Epic Games";
    public string EpicManagementHint => T(
        "EditMedia.EpicManagementHint",
        "Store actions are applied immediately. Successfully changed launch settings are reloaded in this editor.");
    public string EpicInstallText => Strings.Button_Install;
    public string EpicCheckUpdatesText => T("Epic.Update.CheckNow", "Check for Epic Games updates");
    public string EpicUpdateText => Strings.Button_Update;
    public string EpicRepairText => T("Epic.Repair.ContextMenu", "Repair installation");
    public string EpicUninstallText => T("Epic.Uninstall.ContextMenu", "Uninstall");
    public string EpicManageDlcsText => T("Epic.Dlc.ContextMenu", "Manage DLCs...");
    public string EpicDlcTitle => T("EditMedia.EpicDlcTitle", "DLCs");
    public string EpicDlcHint => T(
        "EditMedia.EpicDlcHint",
        "Show DLCs owned and managed through Legendary.");

    public string EpicInstalledVersionText
    {
        get
        {
            var version = EpicMediaItemStateHelper.TryGetInstalledVersion(_originalItem);
            return version == null
                ? string.Empty
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T("EditMedia.EpicInstalledVersionFormat", "Installed version: {0}"),
                    version);
        }
    }

    public string EpicManagementStatusText
    {
        get
        {
            if (ShowEpicUpdateAction)
            {
                return T(
                    "EditMedia.EpicStatusUpdateAvailable",
                    "Installed – update available");
            }

            if (EpicMediaItemStateHelper.IsRetromindManagedInstallation(_originalItem))
                return T("EditMedia.EpicStatusInstalled", "Installed");

            if (EpicMediaItemStateHelper.IsLaunchReady(_originalItem))
            {
                return T(
                    "EditMedia.EpicStatusExternal",
                    "Launch ready – externally managed");
            }

            return T("EditMedia.EpicStatusNotInstalled", "Not installed");
        }
    }

    private void InitializeEpicManagement(
        Func<Window, Task<bool>>? install,
        Func<Window, Task>? checkUpdates,
        Func<Window, Task<bool>>? update,
        Func<Window, Task<bool>>? repair,
        Func<Window, Task<bool>>? uninstall,
        Func<Window, Task>? manageDlcs)
    {
        _epicInstall = install;
        _epicCheckUpdates = checkUpdates;
        _epicUpdate = update;
        _epicRepair = repair;
        _epicUninstall = uninstall;
        _epicManageDlcs = manageDlcs;

        EpicInstallCommand = new AsyncRelayCommand<Window?>(
            owner => RunEpicActionAsync(owner, _epicInstall),
            owner => ShowEpicInstallAction && CanRunEpicAction(owner, _epicInstall));
        EpicCheckUpdatesCommand = new AsyncRelayCommand<Window?>(
            RunEpicUpdateCheckAsync,
            owner => ShowEpicCheckUpdatesAction && CanRunEpicAction(owner, _epicCheckUpdates));
        EpicUpdateCommand = new AsyncRelayCommand<Window?>(
            owner => RunEpicActionAsync(owner, _epicUpdate),
            owner => ShowEpicUpdateAction && CanRunEpicAction(owner, _epicUpdate));
        EpicRepairCommand = new AsyncRelayCommand<Window?>(
            owner => RunEpicActionAsync(owner, _epicRepair),
            owner => ShowEpicRepairAction && CanRunEpicAction(owner, _epicRepair));
        EpicUninstallCommand = new AsyncRelayCommand<Window?>(
            owner => RunEpicActionAsync(owner, _epicUninstall),
            owner => ShowEpicUninstallAction && CanRunEpicAction(owner, _epicUninstall));
        EpicManageDlcsCommand = new AsyncRelayCommand<Window?>(
            RunEpicDlcDialogAsync,
            owner => ShowEpicDlcAction && CanRunEpicAction(owner, _epicManageDlcs));
    }

    private bool CanRunEpicAction(Window? owner, Delegate? action) =>
        owner != null &&
        action != null &&
        IsEpicManagementVisible &&
        !IsEpicOperationRunning;

    private async Task RunEpicActionAsync(
        Window? owner,
        Func<Window, Task<bool>>? action)
    {
        if (!CanRunEpicAction(owner, action) || owner == null || action == null)
            return;

        IsEpicOperationRunning = true;
        try
        {
            if (await action(owner))
                ReloadLaunchConfigurationFromOriginalItem();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Media-editor action failed: {ex.Message}");
        }
        finally
        {
            IsEpicOperationRunning = false;
            RefreshEpicManagementState();
        }
    }

    private async Task RunEpicUpdateCheckAsync(Window? owner)
    {
        if (!CanRunEpicAction(owner, _epicCheckUpdates) || owner == null || _epicCheckUpdates == null)
            return;

        IsEpicOperationRunning = true;
        try
        {
            await _epicCheckUpdates(owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Update check from media editor failed: {ex.Message}");
        }
        finally
        {
            IsEpicOperationRunning = false;
            RefreshEpicManagementState();
        }
    }

    private async Task RunEpicDlcDialogAsync(Window? owner)
    {
        if (!CanRunEpicAction(owner, _epicManageDlcs) || owner == null || _epicManageDlcs == null)
            return;

        IsEpicOperationRunning = true;
        try
        {
            await _epicManageDlcs(owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC dialog from media editor failed: {ex.Message}");
        }
        finally
        {
            IsEpicOperationRunning = false;
            RefreshEpicManagementState();
        }
    }

    private void RefreshEpicManagementState()
    {
        OnPropertyChanged(nameof(IsEpicManagementVisible));
        OnPropertyChanged(nameof(ShowEpicInstallAction));
        OnPropertyChanged(nameof(ShowEpicCheckUpdatesAction));
        OnPropertyChanged(nameof(ShowEpicUpdateAction));
        OnPropertyChanged(nameof(ShowEpicRepairAction));
        OnPropertyChanged(nameof(ShowEpicUninstallAction));
        OnPropertyChanged(nameof(ShowEpicDlcAction));
        OnPropertyChanged(nameof(ShowEpicInstalledVersion));
        OnPropertyChanged(nameof(EpicInstalledVersionText));
        OnPropertyChanged(nameof(EpicManagementStatusText));
        EpicInstallCommand.NotifyCanExecuteChanged();
        EpicCheckUpdatesCommand.NotifyCanExecuteChanged();
        EpicUpdateCommand.NotifyCanExecuteChanged();
        EpicRepairCommand.NotifyCanExecuteChanged();
        EpicUninstallCommand.NotifyCanExecuteChanged();
        EpicManageDlcsCommand.NotifyCanExecuteChanged();
    }
}
