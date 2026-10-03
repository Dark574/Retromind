using Avalonia.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class KeyboardBindingHelperTests
{
    [Theory]
    [InlineData(Key.Up, (int)BigModeKeyboardAction.NavigateUp)]
    [InlineData(Key.Down, (int)BigModeKeyboardAction.NavigateDown)]
    [InlineData(Key.Left, (int)BigModeKeyboardAction.NavigateLeft)]
    [InlineData(Key.Right, (int)BigModeKeyboardAction.NavigateRight)]
    [InlineData(Key.Enter, (int)BigModeKeyboardAction.Select)]
    [InlineData(Key.Space, (int)BigModeKeyboardAction.Select)]
    [InlineData(Key.Back, (int)BigModeKeyboardAction.Back)]
    [InlineData(Key.Escape, (int)BigModeKeyboardAction.ExitBigMode)]
    [InlineData(Key.I, (int)BigModeKeyboardAction.Details)]
    [InlineData(Key.Home, (int)BigModeKeyboardAction.Home)]
    [InlineData(Key.F10, (int)BigModeKeyboardAction.SystemMenu)]
    [InlineData(Key.PageUp, (int)BigModeKeyboardAction.PreviousPage)]
    [InlineData(Key.PageDown, (int)BigModeKeyboardAction.NextPage)]
    public void TryResolveAction_MapsDefaultBindings(Key key, int expected)
    {
        Assert.True(KeyboardBindingHelper.TryResolveAction(new KeyboardBindingSettings(), key, out var actual));
        Assert.Equal(expected, (int)actual);
    }

    [Fact]
    public void TryResolveAction_UsesCustomBindingInsteadOfItsDefault()
    {
        var bindings = new KeyboardBindingSettings { SystemMenu = "F9" };

        Assert.True(KeyboardBindingHelper.TryResolveAction(bindings, Key.F9, out var actual));
        Assert.Equal(BigModeKeyboardAction.SystemMenu, actual);
        Assert.False(KeyboardBindingHelper.TryResolveAction(bindings, Key.F10, out _));
    }

    [Fact]
    public void Resolve_InvalidStoredKeyFallsBackSafely()
    {
        Assert.Equal(Key.PageUp, KeyboardBindingHelper.Resolve("not-a-key", Key.PageUp));
        Assert.Equal(Key.PageDown, KeyboardBindingHelper.Resolve(null, Key.PageDown));
    }
}
