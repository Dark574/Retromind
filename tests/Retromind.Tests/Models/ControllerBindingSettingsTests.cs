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
        Assert.Equal(ControllerButton.Start, settings.ControllerBindings.SystemMenu);
        Assert.Equal(ControllerButton.LeftShoulder, settings.ControllerBindings.PreviousPage);
        Assert.Equal(ControllerButton.RightShoulder, settings.ControllerBindings.NextPage);
        Assert.Equal(ControllerButton.LeftShoulder, settings.ControllerBindings.SessionStopFirst);
        Assert.Equal(ControllerButton.RightShoulder, settings.ControllerBindings.SessionStopSecond);
        Assert.Equal(ControllerButton.Unassigned, settings.ControllerBindings.Alphabet);
    }

    [Theory]
    [InlineData(ControllerButton.Unassigned)]
    [InlineData(ControllerButton.RightStick)]
    [InlineData(ControllerButton.LeftTrigger)]
    public void AlphabetBinding_RoundTripsWithoutChangingOldAssignments(ControllerButton button)
    {
        var settings = new AppSettings();
        settings.ControllerBindings.Alphabet = button;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(button, restored.ControllerBindings.Alphabet);
        Assert.Equal(ControllerButton.South, restored.ControllerBindings.Select);
        Assert.Equal(ControllerButton.LeftShoulder, restored.ControllerBindings.PreviousPage);
    }
}
