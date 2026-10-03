using Retromind.Models;
using Retromind.Services;
using Silk.NET.SDL;

namespace Retromind.Tests.Services;

public sealed class GamepadServiceTests
{
    [Theory]
    [InlineData(GameControllerButton.A, ControllerButton.South)]
    [InlineData(GameControllerButton.B, ControllerButton.East)]
    [InlineData(GameControllerButton.X, ControllerButton.West)]
    [InlineData(GameControllerButton.Y, ControllerButton.North)]
    [InlineData(GameControllerButton.Back, ControllerButton.Back)]
    [InlineData(GameControllerButton.Guide, ControllerButton.Guide)]
    [InlineData(GameControllerButton.Start, ControllerButton.Start)]
    [InlineData(GameControllerButton.Leftstick, ControllerButton.LeftStick)]
    [InlineData(GameControllerButton.Rightstick, ControllerButton.RightStick)]
    [InlineData(GameControllerButton.Leftshoulder, ControllerButton.LeftShoulder)]
    [InlineData(GameControllerButton.Rightshoulder, ControllerButton.RightShoulder)]
    [InlineData(GameControllerButton.Misc1, ControllerButton.Misc)]
    [InlineData(GameControllerButton.Paddle1, ControllerButton.Paddle1)]
    [InlineData(GameControllerButton.Paddle2, ControllerButton.Paddle2)]
    [InlineData(GameControllerButton.Paddle3, ControllerButton.Paddle3)]
    [InlineData(GameControllerButton.Paddle4, ControllerButton.Paddle4)]
    [InlineData(GameControllerButton.Touchpad, ControllerButton.Touchpad)]
    public void TryMapButton_MapsAssignableSdlButtons(
        GameControllerButton source,
        ControllerButton expected)
    {
        Assert.True(GamepadService.TryMapButton(source, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryMapButton_LeavesDirectionalNavigationFixed()
    {
        Assert.False(GamepadService.TryMapButton(GameControllerButton.DpadUp, out _));
    }
}
