using Avalonia.Controls;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class ThemeTests
{
    [Fact]
    public void Constructor_PreservesDisabledSecondaryVideoWhenBackgroundPathExists()
    {
        var theme = new Theme(
            new Border(),
            new ThemeSounds(),
            "/theme",
            secondaryBackgroundVideoPath: "Videos/background.mp4",
            secondaryVideoEnabled: false);

        Assert.False(theme.SecondaryVideoEnabled);
    }
}
