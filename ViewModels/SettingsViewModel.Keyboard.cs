using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

internal enum KeyboardBindingTarget
{
    NavigateUp,
    NavigateDown,
    NavigateLeft,
    NavigateRight,
    Select,
    AlternateSelect,
    Back,
    ExitBigMode,
    Details,
    Home,
    SystemMenu,
    PreviousPage,
    NextPage
}

public sealed partial class KeyboardBindingRowViewModel : ObservableObject
{
    private readonly Action<KeyboardBindingRowViewModel> _requestCapture;
    private readonly string _capturePrompt;

    internal KeyboardBindingTarget Target { get; }
    public string Label { get; }
    public IRelayCommand CaptureCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    private Key _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    private bool _isCapturing;

    public string KeyText => IsCapturing ? _capturePrompt : KeyboardBindingHelper.Format(Key);

    internal KeyboardBindingRowViewModel(
        KeyboardBindingTarget target,
        string label,
        Key key,
        string capturePrompt,
        Action<KeyboardBindingRowViewModel> requestCapture)
    {
        Target = target;
        Label = label;
        _key = key;
        _capturePrompt = capturePrompt;
        _requestCapture = requestCapture;
        CaptureCommand = new RelayCommand(() => _requestCapture(this));
    }
}

public partial class SettingsViewModel
{
    private readonly object _keyboardCaptureGate = new();
    private KeyboardBindingRowViewModel? _keyboardCaptureTarget;

    public ObservableCollection<KeyboardBindingRowViewModel> KeyboardBindings { get; } = [];
    public IRelayCommand ResetKeyboardBindingsCommand { get; private set; } = null!;

    public string KeyboardActionsTitle => T("Settings_KeyboardActions", "Keyboard · BigMode actions");
    public string KeyboardCaptureHint => T(
        "Settings_KeyboardCaptureHint",
        "Select an assignment, then press one key. Modifier combinations are not supported.");
    public string ResetKeyboardBindingsText => T(
        "Settings_KeyboardReset",
        "Restore keyboard defaults");

    private void InitializeKeyboardBindings()
    {
        _appSettings.KeyboardBindings ??= new KeyboardBindingSettings();
        ResetKeyboardBindingsCommand = new RelayCommand(ResetKeyboardBindings);
        RebuildKeyboardBindingRows();
    }

    private void RebuildKeyboardBindingRows()
    {
        CancelKeyboardCapture();
        KeyboardBindings.Clear();

        var bindings = _appSettings.KeyboardBindings;
        var capturePrompt = T("Settings_KeyboardCapturePrompt", "Press a key …");

        Add(KeyboardBindingTarget.NavigateUp, T("Settings_KeyboardNavigateUp", "Navigate up"), bindings.NavigateUp, Key.Up);
        Add(KeyboardBindingTarget.NavigateDown, T("Settings_KeyboardNavigateDown", "Navigate down"), bindings.NavigateDown, Key.Down);
        Add(KeyboardBindingTarget.NavigateLeft, T("Settings_KeyboardNavigateLeft", "Navigate left"), bindings.NavigateLeft, Key.Left);
        Add(KeyboardBindingTarget.NavigateRight, T("Settings_KeyboardNavigateRight", "Navigate right"), bindings.NavigateRight, Key.Right);
        Add(KeyboardBindingTarget.Select, T("Settings_ControllerSelect", "Select / start"), bindings.Select, Key.Enter);
        Add(KeyboardBindingTarget.AlternateSelect, T("Settings_KeyboardAlternateSelect", "Select / start (alternative)"), bindings.AlternateSelect, Key.Space);
        Add(KeyboardBindingTarget.Back, T("Settings_ControllerBack", "Back"), bindings.Back, Key.Back);
        Add(KeyboardBindingTarget.ExitBigMode, T("Settings_ControllerExitBigMode", "Exit BigMode"), bindings.ExitBigMode, Key.Escape);
        Add(KeyboardBindingTarget.Details, T("Settings_ControllerDetails", "Details / achievements"), bindings.Details, Key.I);
        Add(KeyboardBindingTarget.Home, T("Settings_ControllerHome", "Home screen"), bindings.Home, Key.Home);
        Add(KeyboardBindingTarget.SystemMenu, T("Settings_ControllerSystemMenu", "System menu"), bindings.SystemMenu, Key.F10);
        Add(KeyboardBindingTarget.PreviousPage, T("Settings_ControllerPreviousPage", "Previous page"), bindings.PreviousPage, Key.PageUp);
        Add(KeyboardBindingTarget.NextPage, T("Settings_ControllerNextPage", "Next page"), bindings.NextPage, Key.PageDown);

        void Add(
            KeyboardBindingTarget target,
            string label,
            string? storedKey,
            Key fallback) =>
            KeyboardBindings.Add(new KeyboardBindingRowViewModel(
                target,
                label,
                KeyboardBindingHelper.Resolve(storedKey, fallback),
                capturePrompt,
                BeginKeyboardCapture));
    }

    private void BeginKeyboardCapture(KeyboardBindingRowViewModel row)
    {
        CancelControllerCapture();

        KeyboardBindingRowViewModel? previous;
        var cancelCurrent = false;
        lock (_keyboardCaptureGate)
        {
            previous = _keyboardCaptureTarget;
            if (ReferenceEquals(previous, row))
            {
                _keyboardCaptureTarget = null;
                cancelCurrent = true;
            }
            else
            {
                _keyboardCaptureTarget = row;
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

    public bool TryCompleteKeyboardCapture(Key key, KeyModifiers modifiers)
    {
        KeyboardBindingRowViewModel? target;
        lock (_keyboardCaptureGate)
        {
            target = _keyboardCaptureTarget;
            if (target == null)
                return false;

            if (key == Key.None || modifiers != KeyModifiers.None)
                return true;

            _keyboardCaptureTarget = null;
        }

        CompleteKeyboardCapture(target, key);
        return true;
    }

    internal void CompleteKeyboardCapture(KeyboardBindingRowViewModel row, Key key)
    {
        if (_disposed || key == Key.None)
            return;

        row.IsCapturing = false;
        var conflict = KeyboardBindings.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, row) && candidate.Key == key);

        if (conflict != null)
            conflict.Key = row.Key;

        row.Key = key;
        WriteKeyboardBindingsToWorkingCopy();
    }

    private void ResetKeyboardBindings()
    {
        _appSettings.KeyboardBindings = new KeyboardBindingSettings();
        RebuildKeyboardBindingRows();
    }

    private void WriteKeyboardBindingsToWorkingCopy()
    {
        var bindings = _appSettings.KeyboardBindings;
        foreach (var row in KeyboardBindings)
        {
            var storedKey = KeyboardBindingHelper.ToStorage(row.Key);
            switch (row.Target)
            {
                case KeyboardBindingTarget.NavigateUp:
                    bindings.NavigateUp = storedKey;
                    break;
                case KeyboardBindingTarget.NavigateDown:
                    bindings.NavigateDown = storedKey;
                    break;
                case KeyboardBindingTarget.NavigateLeft:
                    bindings.NavigateLeft = storedKey;
                    break;
                case KeyboardBindingTarget.NavigateRight:
                    bindings.NavigateRight = storedKey;
                    break;
                case KeyboardBindingTarget.Select:
                    bindings.Select = storedKey;
                    break;
                case KeyboardBindingTarget.AlternateSelect:
                    bindings.AlternateSelect = storedKey;
                    break;
                case KeyboardBindingTarget.Back:
                    bindings.Back = storedKey;
                    break;
                case KeyboardBindingTarget.ExitBigMode:
                    bindings.ExitBigMode = storedKey;
                    break;
                case KeyboardBindingTarget.Details:
                    bindings.Details = storedKey;
                    break;
                case KeyboardBindingTarget.Home:
                    bindings.Home = storedKey;
                    break;
                case KeyboardBindingTarget.SystemMenu:
                    bindings.SystemMenu = storedKey;
                    break;
                case KeyboardBindingTarget.PreviousPage:
                    bindings.PreviousPage = storedKey;
                    break;
                case KeyboardBindingTarget.NextPage:
                    bindings.NextPage = storedKey;
                    break;
            }
        }
    }

    private void CancelKeyboardCapture()
    {
        KeyboardBindingRowViewModel? target;
        lock (_keyboardCaptureGate)
        {
            target = _keyboardCaptureTarget;
            _keyboardCaptureTarget = null;
        }

        if (target != null)
            target.IsCapturing = false;
    }

    private void DisposeKeyboardBindings() => CancelKeyboardCapture();
}
