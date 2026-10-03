using System.Text.Json;
using Retromind.Models;

namespace Retromind.Tests.Models;

public sealed class ControllerBindingSettingsTests
{
    [Fact]
    public void MissingPersistedBindings_UseCurrentDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.Equal(ControllerButton.South, settings.ControllerBindings.Select);
        Assert.Equal(ControllerButton.East, settings.ControllerBindings.Back);
        Assert.Equal(ControllerButton.Guide, settings.ControllerBindings.ExitBigMode);
        Assert.Equal(ControllerButton.LeftShoulder, settings.ControllerBindings.SessionStopFirst);
        Assert.Equal(ControllerButton.RightShoulder, settings.ControllerBindings.SessionStopSecond);
    }
}
