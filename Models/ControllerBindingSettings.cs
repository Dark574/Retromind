namespace Retromind.Models;

/// <summary>
/// Stable, SDL-independent names for assignable controller buttons.
/// Face-button names describe their standardized physical position.
/// </summary>
public enum ControllerButton
{
    South = 0,
    East = 1,
    West = 2,
    North = 3,
    Back = 4,
    Guide = 5,
    Start = 6,
    LeftStick = 7,
    RightStick = 8,
    LeftShoulder = 9,
    RightShoulder = 10,
    LeftTrigger = 11,
    RightTrigger = 12,
    Misc = 13,
    Paddle1 = 14,
    Paddle2 = 15,
    Paddle3 = 16,
    Paddle4 = 17,
    Touchpad = 18
}

/// <summary>
/// User-configurable BigMode controller bindings. Directional navigation remains
/// fixed to the D-pad and left stick.
/// </summary>
public sealed class ControllerBindingSettings
{
    public ControllerButton Select { get; set; } = ControllerButton.South;
    public ControllerButton Back { get; set; } = ControllerButton.East;
    public ControllerButton Details { get; set; } = ControllerButton.West;
    public ControllerButton Home { get; set; } = ControllerButton.North;
    public ControllerButton ExitBigMode { get; set; } = ControllerButton.Guide;
    public ControllerButton SystemMenu { get; set; } = ControllerButton.Start;
    public ControllerButton SessionStopFirst { get; set; } = ControllerButton.LeftShoulder;
    public ControllerButton SessionStopSecond { get; set; } = ControllerButton.RightShoulder;
}
