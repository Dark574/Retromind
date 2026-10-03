using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

internal enum ControllerBindingTarget
{
    Select,
    Back,
    Details,
    Home,
    ExitBigMode,
    SessionStopFirst,
    SessionStopSecond
}

public sealed partial class ControllerBindingRowViewModel : ObservableObject
{
    private readonly Action<ControllerBindingRowViewModel> _requestCapture;
    private readonly Func<ControllerButton, string> _formatButton;
    private readonly string _capturePrompt;

    internal ControllerBindingTarget Target { get; }
    internal bool IsSessionStopButton =>
        Target is ControllerBindingTarget.SessionStopFirst or ControllerBindingTarget.SessionStopSecond;

    public string Label { get; }
    public IRelayCommand CaptureCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private ControllerButton _button;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isCapturing;

    public string ButtonText => IsCapturing ? _capturePrompt : _formatButton(Button);

    internal ControllerBindingRowViewModel(
        ControllerBindingTarget target,
        string label,
        ControllerButton button,
        string capturePrompt,
        Func<ControllerButton, string> formatButton,
        Action<ControllerBindingRowViewModel> requestCapture)
    {
        Target = target;
        Label = label;
        _button = button;
        _capturePrompt = capturePrompt;
        _formatButton = formatButton;
        _requestCapture = requestCapture;
        CaptureCommand = new RelayCommand(() => _requestCapture(this));
    }
}

public partial class SettingsViewModel
{
    private readonly object _controllerCaptureGate = new();
    private readonly GamepadService? _gamepadService;
    private ControllerBindingRowViewModel? _controllerCaptureTarget;

    public ObservableCollection<ControllerBindingRowViewModel> ControllerActionBindings { get; } = new();
    public ObservableCollection<ControllerBindingRowViewModel> ControllerSessionStopBindings { get; } = new();
    public IRelayCommand ResetControllerBindingsCommand { get; private set; } = null!;

    public string SettingsTabControllerTitle => T("Settings_ControllerTab", "Controller");
    public string ControllerActionsTitle => T("Settings_ControllerActions", "BigMode actions");
    public string ControllerNavigationHint => T(
        "Settings_ControllerNavigationHint",
        "The D-pad and left stick remain assigned to navigation.");
    public string ControllerSessionStopTitle => T(
        "Settings_ControllerSessionStop",
        "Stop a running game");
    public string ControllerSessionStopHint => T(
        "Settings_ControllerSessionStopHint",
        "Hold both buttons for two seconds. Release and hold them again to confirm a forced stop.");
    public string ResetControllerBindingsText => T(
        "Settings_ControllerReset",
        "Restore defaults");

    private void InitializeControllerBindings()
    {
        _appSettings.ControllerBindings ??= new ControllerBindingSettings();
        ResetControllerBindingsCommand = new RelayCommand(ResetControllerBindings);
        RebuildControllerBindingRows();

        if (_gamepadService != null)
            _gamepadService.OnButtonPressed += OnControllerButtonPressed;
    }

    private void RebuildControllerBindingRows()
    {
        CancelControllerCapture();
        ControllerActionBindings.Clear();
        ControllerSessionStopBindings.Clear();

        var bindings = _appSettings.ControllerBindings;
        var capturePrompt = T("Settings_ControllerCapturePrompt", "Press a controller button …");

        AddActionRow(ControllerBindingTarget.Select, T("Settings_ControllerSelect", "Select / start"), bindings.Select);
        AddActionRow(ControllerBindingTarget.Back, T("Settings_ControllerBack", "Back"), bindings.Back);
        AddActionRow(ControllerBindingTarget.Details, T("Settings_ControllerDetails", "Details / achievements"), bindings.Details);
        AddActionRow(ControllerBindingTarget.Home, T("Settings_ControllerHome", "Home screen"), bindings.Home);
        AddActionRow(ControllerBindingTarget.ExitBigMode, T("Settings_ControllerExitBigMode", "Exit BigMode"), bindings.ExitBigMode);

        ControllerSessionStopBindings.Add(CreateRow(
            ControllerBindingTarget.SessionStopFirst,
            T("Settings_ControllerSessionStopFirst", "First button"),
            bindings.SessionStopFirst,
            capturePrompt));
        ControllerSessionStopBindings.Add(CreateRow(
            ControllerBindingTarget.SessionStopSecond,
            T("Settings_ControllerSessionStopSecond", "Second button"),
            bindings.SessionStopSecond,
            capturePrompt));

        void AddActionRow(ControllerBindingTarget target, string label, ControllerButton button) =>
            ControllerActionBindings.Add(CreateRow(target, label, button, capturePrompt));
    }

    private ControllerBindingRowViewModel CreateRow(
        ControllerBindingTarget target,
        string label,
        ControllerButton button,
        string capturePrompt) =>
        new(
            target,
            label,
            button,
            capturePrompt,
            FormatControllerButton,
            BeginControllerCapture);

    private void BeginControllerCapture(ControllerBindingRowViewModel row)
    {
        ControllerBindingRowViewModel? previous;
        var cancelCurrent = false;
        lock (_controllerCaptureGate)
        {
            previous = _controllerCaptureTarget;
            if (ReferenceEquals(previous, row))
            {
                _controllerCaptureTarget = null;
                cancelCurrent = true;
            }
            else
            {
                _controllerCaptureTarget = row;
            }
        }

        if (cancelCurrent)
        {
            row.IsCapturing = false;
            return;
        }

        if (previous != null && !ReferenceEquals(previous, row))
            previous.IsCapturing = false;

        row.IsCapturing = true;
    }

    private void OnControllerButtonPressed(ControllerButton button)
    {
        ControllerBindingRowViewModel? target;
        lock (_controllerCaptureGate)
        {
            target = _controllerCaptureTarget;
            _controllerCaptureTarget = null;
        }

        if (target == null)
            return;

        UiThreadHelper.Post(() => CompleteControllerCapture(target, button));
    }

    internal void CompleteControllerCapture(ControllerBindingRowViewModel row, ControllerButton button)
    {
        if (_disposed)
            return;

        row.IsCapturing = false;
        var group = row.IsSessionStopButton
            ? ControllerSessionStopBindings
            : ControllerActionBindings;
        var conflict = group.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, row) && candidate.Button == button);

        if (conflict != null)
            conflict.Button = row.Button;

        row.Button = button;
        WriteControllerBindingsToWorkingCopy();
    }

    private void ResetControllerBindings()
    {
        _appSettings.ControllerBindings = new ControllerBindingSettings();
        RebuildControllerBindingRows();
    }

    private void WriteControllerBindingsToWorkingCopy()
    {
        var bindings = _appSettings.ControllerBindings;
        foreach (var row in ControllerActionBindings.Concat(ControllerSessionStopBindings))
        {
            switch (row.Target)
            {
                case ControllerBindingTarget.Select:
                    bindings.Select = row.Button;
                    break;
                case ControllerBindingTarget.Back:
                    bindings.Back = row.Button;
                    break;
                case ControllerBindingTarget.Details:
                    bindings.Details = row.Button;
                    break;
                case ControllerBindingTarget.Home:
                    bindings.Home = row.Button;
                    break;
                case ControllerBindingTarget.ExitBigMode:
                    bindings.ExitBigMode = row.Button;
                    break;
                case ControllerBindingTarget.SessionStopFirst:
                    bindings.SessionStopFirst = row.Button;
                    break;
                case ControllerBindingTarget.SessionStopSecond:
                    bindings.SessionStopSecond = row.Button;
                    break;
            }
        }
    }

    private void CancelControllerCapture()
    {
        ControllerBindingRowViewModel? target;
        lock (_controllerCaptureGate)
        {
            target = _controllerCaptureTarget;
            _controllerCaptureTarget = null;
        }

        if (target != null)
            target.IsCapturing = false;
    }

    private void DisposeControllerBindings()
    {
        CancelControllerCapture();
        if (_gamepadService != null)
            _gamepadService.OnButtonPressed -= OnControllerButtonPressed;
    }

    private string FormatControllerButton(ControllerButton button) => button switch
    {
        ControllerButton.South => T("ControllerButton_South", "A / Cross (bottom)"),
        ControllerButton.East => T("ControllerButton_East", "B / Circle (right)"),
        ControllerButton.West => T("ControllerButton_West", "X / Square (left)"),
        ControllerButton.North => T("ControllerButton_North", "Y / Triangle (top)"),
        ControllerButton.Back => T("ControllerButton_Back", "Select / View / Share"),
        ControllerButton.Guide => T("ControllerButton_Guide", "Guide / Home / PS"),
        ControllerButton.Start => T("ControllerButton_Start", "Start / Menu / Options"),
        ControllerButton.LeftStick => "L3",
        ControllerButton.RightStick => "R3",
        ControllerButton.LeftShoulder => "L1 / LB",
        ControllerButton.RightShoulder => "R1 / RB",
        ControllerButton.LeftTrigger => "L2 / LT",
        ControllerButton.RightTrigger => "R2 / RT",
        ControllerButton.Misc => T("ControllerButton_Misc", "Share / Capture / Misc"),
        ControllerButton.Paddle1 => T("ControllerButton_Paddle1", "Paddle 1"),
        ControllerButton.Paddle2 => T("ControllerButton_Paddle2", "Paddle 2"),
        ControllerButton.Paddle3 => T("ControllerButton_Paddle3", "Paddle 3"),
        ControllerButton.Paddle4 => T("ControllerButton_Paddle4", "Paddle 4"),
        ControllerButton.Touchpad => T("ControllerButton_Touchpad", "Touchpad click"),
        _ => button.ToString()
    };
}
