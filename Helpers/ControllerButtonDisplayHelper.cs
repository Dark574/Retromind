using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Helpers;

public static class ControllerButtonDisplayHelper
{
    public static string Format(ControllerButton button) => button switch
    {
        ControllerButton.South => T("ControllerButton_South", "A / Cross (bottom)"),
        ControllerButton.East => T("ControllerButton_East", "B / Circle (right)"),
        ControllerButton.West => T("ControllerButton_West", "X / Square (left)"),
        ControllerButton.North => T("ControllerButton_North", "Y / Triangle (top)"),
        ControllerButton.Back => T("ControllerButton_Back", "Select / View / Share"),
        ControllerButton.Guide => T("ControllerButton_Guide", "Guide / Home / PS"),
        ControllerButton.Start => T("ControllerButton_Start", "Start / Menu / Options"),
        ControllerButton.LeftStick => "L3",
        ControllerButton.RightStick => "R3",
        ControllerButton.LeftShoulder => "L1 / LB",
        ControllerButton.RightShoulder => "R1 / RB",
        ControllerButton.LeftTrigger => "L2 / LT",
        ControllerButton.RightTrigger => "R2 / RT",
        ControllerButton.Misc => T("ControllerButton_Misc", "Share / Capture / Misc"),
        ControllerButton.Paddle1 => T("ControllerButton_Paddle1", "Paddle 1"),
        ControllerButton.Paddle2 => T("ControllerButton_Paddle2", "Paddle 2"),
        ControllerButton.Paddle3 => T("ControllerButton_Paddle3", "Paddle 3"),
        ControllerButton.Paddle4 => T("ControllerButton_Paddle4", "Paddle 4"),
        ControllerButton.Touchpad => T("ControllerButton_Touchpad", "Touchpad click"),
        _ => button.ToString()
    };

    private static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
