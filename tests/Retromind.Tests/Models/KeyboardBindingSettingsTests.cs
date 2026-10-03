using System.Text.Json;
using Retromind.Models;

namespace Retromind.Tests.Models;

public sealed class KeyboardBindingSettingsTests
{
    [Fact]
    public void MissingPersistedBindings_UseCurrentDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.Equal("Up", settings.KeyboardBindings.NavigateUp);
        Assert.Equal("Down", settings.KeyboardBindings.NavigateDown);
        Assert.Equal("Left", settings.KeyboardBindings.NavigateLeft);
        Assert.Equal("Right", settings.KeyboardBindings.NavigateRight);
        Assert.Equal("Enter", settings.KeyboardBindings.Select);
        Assert.Equal("Space", settings.KeyboardBindings.AlternateSelect);
        Assert.Equal("Back", settings.KeyboardBindings.Back);
        Assert.Equal("Escape", settings.KeyboardBindings.ExitBigMode);
        Assert.Equal("I", settings.KeyboardBindings.Details);
        Assert.Equal("Home", settings.KeyboardBindings.Home);
        Assert.Equal("F10", settings.KeyboardBindings.SystemMenu);
        Assert.Equal("PageUp", settings.KeyboardBindings.PreviousPage);
        Assert.Equal("PageDown", settings.KeyboardBindings.NextPage);
    }
}
