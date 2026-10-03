using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public enum BigModeSystemMenuEntryKind
{
    Resume,
    ControllerBindings,
    ExitBigMode,
    ExitApplication
}

public enum BigModeSystemMenuPage
{
    Main,
    ControllerBindings,
    ExitConfirmation
}

public sealed record BigModeSystemMenuEntryViewModel(
    BigModeSystemMenuEntryKind Kind,
    string Title,
    string Description);

public sealed record BigModeControllerBindingInfoViewModel(string Action, string Input);

public partial class BigModeViewModel
{
    private bool _isApplicationExitRequested;

    [ObservableProperty]
    private bool _isSystemMenuOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemMenuMainPage))]
    [NotifyPropertyChangedFor(nameof(IsControllerBindingsPage))]
    [NotifyPropertyChangedFor(nameof(IsExitConfirmationPage))]
    private BigModeSystemMenuPage _systemMenuPage;

    [ObservableProperty]
    private BigModeSystemMenuEntryViewModel? _selectedSystemMenuEntry;

    [ObservableProperty]
    private int _selectedExitConfirmationIndex;

    public ObservableCollection<BigModeSystemMenuEntryViewModel> SystemMenuEntries { get; } = [];
    public ObservableCollection<BigModeControllerBindingInfoViewModel> ControllerBindingOverview { get; } = [];
    public ObservableCollection<string> ExitConfirmationOptions { get; } = [];

    public bool IsSystemMenuMainPage => SystemMenuPage == BigModeSystemMenuPage.Main;
    public bool IsControllerBindingsPage => SystemMenuPage == BigModeSystemMenuPage.ControllerBindings;
    public bool IsExitConfirmationPage => SystemMenuPage == BigModeSystemMenuPage.ExitConfirmation;

    public string SystemMenuTitle => T("BigMode_SystemMenuTitle", "System menu");
    public string SystemMenuHintText => string.Format(
        T("BigMode_SystemMenuHintFormat", "{0} · System menu"),
        ControllerButtonDisplayHelper.Format(_settings.ControllerBindings.SystemMenu));
    public string ControllerBindingsTitle => T("BigMode_ControllerBindingsTitle", "Controller layout");
    public string ControllerBindingsHint => T(
        "BigMode_ControllerBindingsHint",
        "These assignments can be changed under Settings → Controller.");
    public string ExitConfirmationTitle => T("BigMode_ExitConfirmationTitle", "Exit Retromind?");
    public string ExitConfirmationText => T(
        "BigMode_ExitConfirmationText",
        "Retromind will save pending changes before closing.");
    public string SystemMenuControlsText => string.Format(
        T("BigMode_SystemMenuControlsFormat", "{0} · Select    {1} · Back"),
        ControllerButtonDisplayHelper.Format(_settings.ControllerBindings.Select),
        ControllerButtonDisplayHelper.Format(_settings.ControllerBindings.Back));

    public event Func<Task>? RequestApplicationExit;

    private void InitializeSystemMenu()
    {
        SystemMenuEntries.Add(new BigModeSystemMenuEntryViewModel(
            BigModeSystemMenuEntryKind.Resume,
            T("BigMode_SystemMenuResume", "Resume"),
            T("BigMode_SystemMenuResumeHint", "Return to BigMode")));
        SystemMenuEntries.Add(new BigModeSystemMenuEntryViewModel(
            BigModeSystemMenuEntryKind.ControllerBindings,
            T("BigMode_SystemMenuController", "Controller layout"),
            T("BigMode_SystemMenuControllerHint", "Show the current assignments")));
        SystemMenuEntries.Add(new BigModeSystemMenuEntryViewModel(
            BigModeSystemMenuEntryKind.ExitBigMode,
            T("BigMode_SystemMenuExitBigMode", "Exit BigMode"),
            T("BigMode_SystemMenuExitBigModeHint", "Return to the desktop library")));
        SystemMenuEntries.Add(new BigModeSystemMenuEntryViewModel(
            BigModeSystemMenuEntryKind.ExitApplication,
            T("BigMode_SystemMenuExitApplication", "Exit Retromind"),
            T("BigMode_SystemMenuExitApplicationHint", "Save and close the application")));

        ExitConfirmationOptions.Add(T("BigMode_ExitConfirmationCancel", "Cancel"));
        ExitConfirmationOptions.Add(T("BigMode_ExitConfirmationConfirm", "Exit Retromind"));
        SelectedSystemMenuEntry = SystemMenuEntries[0];
        RebuildControllerBindingOverview();
    }

    private void OnGamepadSystemMenu() => DispatchGamepadAction(ToggleSystemMenu);

    public void ToggleSystemMenu()
    {
        if (_isLaunching)
            return;

        if (IsSystemMenuOpen)
        {
            CloseSystemMenu();
            return;
        }

        ResetAttractIdleTimer();
        CloseAchievementsOverlay();
        StopGamepadRepeatTimer();
        RebuildControllerBindingOverview();
        SystemMenuPage = BigModeSystemMenuPage.Main;
        SelectedSystemMenuEntry = SystemMenuEntries.Count > 0 ? SystemMenuEntries[0] : null;
        SelectedExitConfirmationIndex = 0;
        IsSystemMenuOpen = true;
    }

    public bool CloseSystemMenu()
    {
        if (!IsSystemMenuOpen)
            return false;

        IsSystemMenuOpen = false;
        SystemMenuPage = BigModeSystemMenuPage.Main;
        SelectedExitConfirmationIndex = 0;
        ResetAttractIdleTimer();
        return true;
    }

    public void NavigateSystemMenu(GamepadService.GamepadDirection direction)
    {
        if (!IsSystemMenuOpen)
            return;

        if (SystemMenuPage == BigModeSystemMenuPage.ControllerBindings)
            return;

        PlaySound(_theme.Sounds.Navigate);

        if (SystemMenuPage == BigModeSystemMenuPage.ExitConfirmation)
        {
            SelectedExitConfirmationIndex = SelectedExitConfirmationIndex == 0 ? 1 : 0;
            return;
        }

        if (SystemMenuEntries.Count == 0)
            return;

        var index = SelectedSystemMenuEntry == null
            ? 0
            : SystemMenuEntries.IndexOf(SelectedSystemMenuEntry);
        if (index < 0)
            index = 0;

        var delta = direction is GamepadService.GamepadDirection.Up or GamepadService.GamepadDirection.Left
            ? -1
            : 1;
        index = (index + delta + SystemMenuEntries.Count) % SystemMenuEntries.Count;
        SelectedSystemMenuEntry = SystemMenuEntries[index];
    }

    public void ActivateSystemMenuSelection()
    {
        if (!IsSystemMenuOpen)
            return;

        PlaySound(_theme.Sounds.Confirm);

        if (SystemMenuPage == BigModeSystemMenuPage.ControllerBindings)
        {
            SystemMenuPage = BigModeSystemMenuPage.Main;
            return;
        }

        if (SystemMenuPage == BigModeSystemMenuPage.ExitConfirmation)
        {
            if (SelectedExitConfirmationIndex == 0)
            {
                SystemMenuPage = BigModeSystemMenuPage.Main;
                return;
            }

            RequestApplicationExitOnce();
            return;
        }

        switch (SelectedSystemMenuEntry?.Kind)
        {
            case BigModeSystemMenuEntryKind.Resume:
                CloseSystemMenu();
                break;
            case BigModeSystemMenuEntryKind.ControllerBindings:
                SystemMenuPage = BigModeSystemMenuPage.ControllerBindings;
                break;
            case BigModeSystemMenuEntryKind.ExitBigMode:
                CloseSystemMenu();
                _ = RequestBigModeCloseAsync();
                break;
            case BigModeSystemMenuEntryKind.ExitApplication:
                SelectedExitConfirmationIndex = 0;
                SystemMenuPage = BigModeSystemMenuPage.ExitConfirmation;
                break;
        }
    }

    public bool HandleSystemMenuBack()
    {
        if (!IsSystemMenuOpen)
            return false;

        PlaySound(_theme.Sounds.Cancel);
        if (SystemMenuPage != BigModeSystemMenuPage.Main)
        {
            SystemMenuPage = BigModeSystemMenuPage.Main;
            return true;
        }

        CloseSystemMenu();
        return true;
    }

    private void RequestApplicationExitOnce()
    {
        if (_isApplicationExitRequested)
            return;

        _isApplicationExitRequested = true;
        var requestExit = RequestApplicationExit;
        if (requestExit == null)
        {
            _isApplicationExitRequested = false;
            return;
        }

        _ = RequestApplicationExitAsync(requestExit);
    }

    private async Task RequestApplicationExitAsync(Func<Task> requestExit)
    {
        try
        {
            await requestExit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BigMode] Application exit request failed: {ex.Message}");
        }
        finally
        {
            _isApplicationExitRequested = false;
        }
    }

    private void RebuildControllerBindingOverview()
    {
        var bindings = _settings.ControllerBindings;
        ControllerBindingOverview.Clear();
        ControllerBindingOverview.Add(new BigModeControllerBindingInfoViewModel(
            T("BigMode_ControllerNavigate", "Navigate"),
            T("BigMode_ControllerNavigateInput", "D-pad / left stick")));
        Add(T("Settings_ControllerSelect", "Select / start"), bindings.Select);
        Add(T("Settings_ControllerBack", "Back"), bindings.Back);
        Add(T("Settings_ControllerDetails", "Details / achievements"), bindings.Details);
        Add(T("Settings_ControllerHome", "Home screen"), bindings.Home);
        Add(T("Settings_ControllerExitBigMode", "Exit BigMode"), bindings.ExitBigMode);
        Add(T("Settings_ControllerSystemMenu", "System menu"), bindings.SystemMenu);
        ControllerBindingOverview.Add(new BigModeControllerBindingInfoViewModel(
            T("Settings_ControllerSessionStop", "Stop a running game"),
            $"{ControllerButtonDisplayHelper.Format(bindings.SessionStopFirst)} + " +
            ControllerButtonDisplayHelper.Format(bindings.SessionStopSecond)));

        void Add(string action, ControllerButton button) =>
            ControllerBindingOverview.Add(new BigModeControllerBindingInfoViewModel(
                action,
                ControllerButtonDisplayHelper.Format(button)));
    }
}
