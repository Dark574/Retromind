using Avalonia.Input;
using Retromind.Helpers;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    public bool HandleKeyboardKeyDown(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None ||
            !KeyboardBindingHelper.TryResolveAction(_settings.KeyboardBindings, key, out var action))
        {
            return false;
        }

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
        }

        return true;
    }

    public bool HandleKeyboardKeyUp(Key key)
    {
        if (!KeyboardBindingHelper.TryResolveAction(_settings.KeyboardBindings, key, out var action) ||
            action is not (BigModeKeyboardAction.NavigateUp or
                BigModeKeyboardAction.NavigateDown or
                BigModeKeyboardAction.NavigateLeft or
                BigModeKeyboardAction.NavigateRight))
        {
            return false;
        }

        if (!IsHomeActive && !IsAchievementsOverlayOpen)
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
