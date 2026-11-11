using System.Globalization;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeSystemMenuHintTests
{
    [Theory]
    [InlineData("de", "Start / F10 → Systemmenü")]
    [InlineData("en", "Start / F10 → System menu")]
    public async Task DefaultHint_UsesCompactNamesAndLocalizedAction(string culture, string expected) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var previous = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo(culture);
                using var fixture = new BigModeNavigationFixture([]);
                Assert.Equal(expected, fixture.Vm.SystemMenuHintText);
            }
            finally { Strings.Culture = previous; }
        });

    [Theory]
    [InlineData(ControllerButton.Back, "F9", "Select / F9 → Systemmenü")]
    [InlineData(ControllerButton.South, "Escape", "A / Esc → Systemmenü")]
    [InlineData(ControllerButton.RightShoulder, "PageDown", "R1 / Bild ↓ → Systemmenü")]
    [InlineData(ControllerButton.Touchpad, "Home", "Touchpad-Klick / Pos1 → Systemmenü")]
    [InlineData(ControllerButton.Paddle1, "f12", "Paddle 1 / F12 → Systemmenü")]
    [InlineData(ControllerButton.LeftStick, "invalid", "L3 / F10 → Systemmenü")]
    public async Task RemappedHint_ReadsCurrentAssignmentsInsteadOfCachingDefaults(
        ControllerButton button, string key, string expected) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var previous = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("de");
                using var fixture = new BigModeNavigationFixture([]);
                Assert.Equal("Start / F10 → Systemmenü", fixture.Vm.SystemMenuHintText);
                fixture.Settings.ControllerBindings.SystemMenu = button;
                fixture.Settings.KeyboardBindings.SystemMenu = key;
                Assert.Equal(expected, fixture.Vm.SystemMenuHintText);
            }
            finally { Strings.Culture = previous; }
        });

    [Fact]
    public async Task ControllerOverview_KeepsDetailedPlatformAliases() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var previous = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("de");
                using var fixture = new BigModeNavigationFixture([]);
                fixture.Call("RebuildControllerBindingOverview");
                var entry = Assert.Single(fixture.Vm.ControllerBindingOverview,
                    entry => entry.Action == fixture.Vm.SystemMenuTitle);
                Assert.Equal("Start / Menü / Options", entry.Input);
                Assert.Equal("Start / F10 → Systemmenü", fixture.Vm.SystemMenuHintText);
            }
            finally { Strings.Culture = previous; }
        });
}
