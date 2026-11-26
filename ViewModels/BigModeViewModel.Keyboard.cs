using System.Collections.Generic;
using Avalonia.Input;
using Retromind.Helpers;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private readonly HashSet<Key> _keyboardScrollKeys = [];
    private readonly HashSet<Key> _pressedKeyboardKeys = [];
    private readonly HashSet<Key> _blockedKeyboardKeys = [];

    public bool HandleKeyboardKeyDown(Key key, KeyModifiers modifiers)
    {
        _pressedKeyboardKeys.Add(key);
        if (_blockedKeyboardKeys.Contains(key)) return true;
        if (modifiers != KeyModifiers.None ||
            !KeyboardBindingHelper.TryResolveAction(_settings.KeyboardBindings, key, out var action))
        {
            return InputContext == BigModeInputContext.Alphabet;
        }

        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
            return true;

        if (InputContext == BigModeInputContext.Alphabet)
        {
            switch (action)
            {
                case BigModeKeyboardAction.NavigateUp: NavigateAlphabet(GamepadService.GamepadDirection.Up); break;
                case BigModeKeyboardAction.NavigateDown: NavigateAlphabet(GamepadService.GamepadDirection.Down); break;
                case BigModeKeyboardAction.NavigateLeft: NavigateAlphabet(GamepadService.GamepadDirection.Left); break;
                case BigModeKeyboardAction.NavigateRight: NavigateAlphabet(GamepadService.GamepadDirection.Right); break;
                case BigModeKeyboardAction.Select: ConfirmAlphabetNavigation(); break;
                case BigModeKeyboardAction.Back:
                case BigModeKeyboardAction.ExitBigMode:
                case BigModeKeyboardAction.Alphabet: CloseAlphabetNavigation(); break;
                case BigModeKeyboardAction.SystemMenu: ToggleSystemMenu(); break;
            }
            return true;
        }

        if (action is (BigModeKeyboardAction.NavigateUp or BigModeKeyboardAction.NavigateDown or
            BigModeKeyboardAction.NavigateLeft or BigModeKeyboardAction.NavigateRight) &&
            !IsHomeActive && !IsAchievementsOverlayOpen && !IsSystemMenuOpen)
            _keyboardScrollKeys.Add(key);

        switch (action)
        {
            case BigModeKeyboardAction.NavigateUp:
                NavigateFromKeyboardBinding(GamepadService.GamepadDirection.Up);
                break;
            case BigModeKeyboardAction.NavigateDown:
                NavigateFromKeyboardBinding(GamepadService.GamepadDirection.Down);
                break;
            case BigModeKeyboardAction.NavigateLeft:
                NavigateFromKeyboardBinding(GamepadService.GamepadDirection.Left);
                break;
            case BigModeKeyboardAction.NavigateRight:
                NavigateFromKeyboardBinding(GamepadService.GamepadDirection.Right);
                break;
            case BigModeKeyboardAction.Select:
                if (IsSystemMenuOpen)
                    ActivateSystemMenuSelection();
                else if (!IsAchievementsOverlayOpen)
                    PlayCurrentCommand.Execute(null);
                break;
            case BigModeKeyboardAction.Back:
                if (!HandleSystemMenuBack() && !CloseAchievementsOverlay())
                    ExitBigModeCommand.Execute(null);
                break;
            case BigModeKeyboardAction.ExitBigMode:
                if (!HandleSystemMenuBack() && !CloseAchievementsOverlay())
                    HardExitBigModeCommand.Execute(null);
                break;
            case BigModeKeyboardAction.Details:
                if (!IsSystemMenuOpen)
                    ToggleAchievementsOverlay();
                break;
            case BigModeKeyboardAction.Home:
                if (!IsSystemMenuOpen)
                    ToggleHomeCommand.Execute(null);
                break;
            case BigModeKeyboardAction.SystemMenu:
                ToggleSystemMenu();
                break;
            case BigModeKeyboardAction.PreviousPage:
                NavigatePage(-1);
                break;
            case BigModeKeyboardAction.NextPage:
                NavigatePage(1);
                break;
            case BigModeKeyboardAction.Alphabet:
                OpenAlphabetNavigation();
                break;
        }

        return true;
    }

    public bool HandleKeyboardKeyUp(Key key)
    {
        _pressedKeyboardKeys.Remove(key);
        var wasBlocked = _blockedKeyboardKeys.Remove(key);
        if (!KeyboardBindingHelper.TryResolveAction(_settings.KeyboardBindings, key, out var action) ||
            action is not (BigModeKeyboardAction.NavigateUp or
                BigModeKeyboardAction.NavigateDown or
                BigModeKeyboardAction.NavigateLeft or
                BigModeKeyboardAction.NavigateRight))
        {
            return wasBlocked || InputContext == BigModeInputContext.Alphabet;
        }

        _keyboardScrollKeys.Remove(key);
        if (_keyboardScrollKeys.Count == 0)
            NotifyKeyboardScrollEnd();

        return true;
    }

    private void NavigateFromKeyboardBinding(GamepadService.GamepadDirection direction)
    {
        if (!IsHomeActive && !IsAchievementsOverlayOpen && !IsSystemMenuOpen)
            NotifyKeyboardScrollStart();

        NavigateFromKeyboard(direction);
    }
}
