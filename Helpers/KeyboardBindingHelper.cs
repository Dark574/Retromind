using System;
using Avalonia.Input;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Helpers;

internal enum BigModeKeyboardAction
{
    NavigateUp,
    NavigateDown,
    NavigateLeft,
    NavigateRight,
    Select,
    Back,
    ExitBigMode,
    Details,
    Home,
    SystemMenu,
    PreviousPage,
    NextPage
}

internal static class KeyboardBindingHelper
{
    public static bool TryResolveAction(
        KeyboardBindingSettings? bindings,
        Key key,
        out BigModeKeyboardAction action)
    {
        bindings ??= new KeyboardBindingSettings();

        if (Matches(bindings.NavigateUp, Key.Up, key))
            action = BigModeKeyboardAction.NavigateUp;
        else if (Matches(bindings.NavigateDown, Key.Down, key))
            action = BigModeKeyboardAction.NavigateDown;
        else if (Matches(bindings.NavigateLeft, Key.Left, key))
            action = BigModeKeyboardAction.NavigateLeft;
        else if (Matches(bindings.NavigateRight, Key.Right, key))
            action = BigModeKeyboardAction.NavigateRight;
        else if (Matches(bindings.Select, Key.Enter, key) ||
                 Matches(bindings.AlternateSelect, Key.Space, key))
            action = BigModeKeyboardAction.Select;
        else if (Matches(bindings.Back, Key.Back, key))
            action = BigModeKeyboardAction.Back;
        else if (Matches(bindings.ExitBigMode, Key.Escape, key))
            action = BigModeKeyboardAction.ExitBigMode;
        else if (Matches(bindings.Details, Key.I, key))
            action = BigModeKeyboardAction.Details;
        else if (Matches(bindings.Home, Key.Home, key))
            action = BigModeKeyboardAction.Home;
        else if (Matches(bindings.SystemMenu, Key.F10, key))
            action = BigModeKeyboardAction.SystemMenu;
        else if (Matches(bindings.PreviousPage, Key.PageUp, key))
            action = BigModeKeyboardAction.PreviousPage;
        else if (Matches(bindings.NextPage, Key.PageDown, key))
            action = BigModeKeyboardAction.NextPage;
        else
        {
            action = default;
            return false;
        }

        return true;
    }

    public static Key Resolve(string? storedKey, Key fallback) =>
        Enum.TryParse<Key>(storedKey, ignoreCase: true, out var key) && key != Key.None
            ? key
            : fallback;

    public static string ToStorage(Key key) => key.ToString();

    public static string Format(string? storedKey, Key fallback) =>
        Format(Resolve(storedKey, fallback));

    public static string Format(Key key) => key switch
    {
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.Enter => T("KeyboardKey_Enter", "Enter"),
        Key.Space => T("KeyboardKey_Space", "Space"),
        Key.Back => T("KeyboardKey_Back", "Backspace"),
        Key.Escape => "Esc",
        Key.Home => T("KeyboardKey_Home", "Home"),
        Key.PageUp => T("KeyboardKey_PageUp", "Page Up"),
        Key.PageDown => T("KeyboardKey_PageDown", "Page Down"),
        _ => key.ToString()
    };

    private static bool Matches(string? storedKey, Key fallback, Key pressedKey) =>
        Resolve(storedKey, fallback) == pressedKey;

    private static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
