using System.Diagnostics;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class LaunchEnvironmentServiceTests
{
    [Fact]
    public void ApplyEnvironmentOverrides_TrimsKeysAndResolvesPortablePaths()
    {
        var startInfo = new ProcessStartInfo();
        var overrides = new Dictionary<string, string>
        {
            ["  RETROMIND_TEST_VALUE  "] = "enabled",
            ["PROTONPATH"] = "Emulators/GE-Proton",
            ["   "] = "ignored"
        };

        LaunchEnvironmentService.ApplyEnvironmentOverrides(startInfo, overrides);

        Assert.Equal("enabled", startInfo.EnvironmentVariables["RETROMIND_TEST_VALUE"]);
        Assert.Equal(
            AppPaths.ResolveDataPath("Emulators/GE-Proton"),
            startInfo.EnvironmentVariables["PROTONPATH"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey(""));
    }

    [Fact]
    public void PrepareNativeOrEmulator_AppliesEmulatorThenItemOverrides()
    {
        var startInfo = new ProcessStartInfo();
        var emulator = new EmulatorConfig();
        emulator.EnvironmentOverrides["RETROMIND_TEST_LAYER"] = "emulator";
        var item = new MediaItem("Layered environment");
        item.EnvironmentOverrides["RETROMIND_TEST_LAYER"] = "item";
        var service = new LaunchEnvironmentService();

        service.PrepareNativeOrEmulator(startInfo, item, emulator, environmentOverrides: null);

        Assert.Equal("item", startInfo.EnvironmentVariables["RETROMIND_TEST_LAYER"]);
    }

    [Fact]
    public void PrepareNativeOrEmulator_UsesResolvedOverridesInsteadOfFallbackLayers()
    {
        var startInfo = new ProcessStartInfo();
        var emulator = new EmulatorConfig();
        emulator.EnvironmentOverrides["RETROMIND_TEST_EMULATOR_ONLY"] = "emulator";
        var item = new MediaItem("Resolved environment");
        item.EnvironmentOverrides["RETROMIND_TEST_ITEM_ONLY"] = "item";
        var resolvedOverrides = new Dictionary<string, string>
        {
            ["RETROMIND_TEST_RESOLVED"] = "resolved"
        };
        var service = new LaunchEnvironmentService();

        service.PrepareNativeOrEmulator(startInfo, item, emulator, resolvedOverrides);

        Assert.Equal("resolved", startInfo.EnvironmentVariables["RETROMIND_TEST_RESOLVED"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("RETROMIND_TEST_EMULATOR_ONLY"));
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("RETROMIND_TEST_ITEM_ONLY"));
    }

    [Fact]
    public void PrepareNativeOrEmulator_AppliesItemXdgAfterEmulatorHostMode()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.EnvironmentVariables["XDG_CONFIG_HOME"] = "/inherited/config";
        startInfo.EnvironmentVariables["XDG_DATA_HOME"] = "/inherited/data";
        var emulator = new EmulatorConfig
        {
            XdgMode = EmulatorConfig.XdgOverrideMode.Host
        };
        var item = new MediaItem("XDG override")
        {
            XdgConfigPath = "Home/custom-config"
        };
        var service = new LaunchEnvironmentService();

        service.PrepareNativeOrEmulator(startInfo, item, emulator, environmentOverrides: null);

        Assert.Equal(
            AppPaths.ResolveDataPath("Home/custom-config"),
            startInfo.EnvironmentVariables["XDG_CONFIG_HOME"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("XDG_DATA_HOME"));
    }

    [Fact]
    public void SanitizeAppImageRuntimeEnvironment_RemovesBundledLibrariesAndVlcPlugins()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] =
            "/tmp/retromind-appdir/usr/lib:/usr/lib:/tmp/retromind-appdir/usr/lib/vlc/lib";
        startInfo.EnvironmentVariables["VLC_PLUGIN_PATH"] = "/tmp/retromind-appdir/usr/lib/vlc/plugins";

        LaunchEnvironmentService.SanitizeAppImageRuntimeEnvironment(
            startInfo,
            appImage: "/tmp/Retromind.AppImage",
            appDir: "/tmp/retromind-appdir");

        Assert.Equal("/usr/lib", startInfo.EnvironmentVariables["LD_LIBRARY_PATH"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("VLC_PLUGIN_PATH"));
    }

    [Fact]
    public void SanitizeAppImageRuntimeEnvironment_LeavesShellLaunchUntouched()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true
        };
        startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = "/tmp/retromind-appdir/usr/lib";
        startInfo.EnvironmentVariables["VLC_PLUGIN_PATH"] = "/tmp/plugins";

        LaunchEnvironmentService.SanitizeAppImageRuntimeEnvironment(
            startInfo,
            appImage: "/tmp/Retromind.AppImage",
            appDir: "/tmp/retromind-appdir");

        Assert.Equal(
            "/tmp/retromind-appdir/usr/lib",
            startInfo.EnvironmentVariables["LD_LIBRARY_PATH"]);
        Assert.Equal("/tmp/plugins", startInfo.EnvironmentVariables["VLC_PLUGIN_PATH"]);
    }

    [Fact]
    public void SanitizePortableEnvironment_RemovesOnlyPortableHomeValues()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var portableHome = Path.Combine(AppPaths.DataRoot, "Home");
        var startInfo = new ProcessStartInfo();
        startInfo.EnvironmentVariables["XDG_CONFIG_HOME"] = Path.Combine(portableHome, "config");
        startInfo.EnvironmentVariables["XDG_DATA_HOME"] = "/host/data";
        startInfo.EnvironmentVariables["DOTNET_CLI_HOME"] = portableHome;
        startInfo.EnvironmentVariables["HOME"] = portableHome;

        LaunchEnvironmentService.SanitizePortableEnvironment(startInfo);

        Assert.False(startInfo.EnvironmentVariables.ContainsKey("XDG_CONFIG_HOME"));
        Assert.Equal("/host/data", startInfo.EnvironmentVariables["XDG_DATA_HOME"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("DOTNET_CLI_HOME"));
        if (startInfo.EnvironmentVariables.ContainsKey("HOME"))
        {
            Assert.False(
                startInfo.EnvironmentVariables["HOME"]!.StartsWith(portableHome, StringComparison.Ordinal));
        }
    }
}
