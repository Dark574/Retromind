using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class LaunchPlanBuilderTests
{
    [Fact]
    public void Build_UsesExplicitItemLauncherBeforeInheritedEmulator()
    {
        var item = new MediaItem("Custom launch")
        {
            LauncherPath = "/opt/custom launcher",
            LauncherArgs = "--item \"{file}\""
        };
        var emulator = new EmulatorConfig
        {
            Path = "/opt/inherited-emulator",
            Arguments = "--emulator {file}"
        };
        const string launchFilePath = "/games/Disc One/game.iso";

        var plan = LaunchPlanBuilder.Build(item, emulator, nativeWrappers: null, launchFilePath);

        Assert.Equal("/opt/custom launcher", plan.FileName);
        Assert.Equal("--item \"/games/Disc One/game.iso\"", plan.Arguments);
        Assert.False(plan.UseShellExecute);
    }

    [Fact]
    public void Build_ExpandsInheritedEmulatorPlaceholdersAndAppendsItemArguments()
    {
        var item = new MediaItem("Emulated game")
        {
            LauncherArgs = "--fullscreen"
        };
        var emulator = new EmulatorConfig
        {
            Path = "/usr/bin/example-emulator",
            Arguments = "--dir {fileDir} --name {fileName} --base {fileBase} {file}"
        };
        const string launchFilePath = "/games/PlayStation One/Game Disc.chd";

        var plan = LaunchPlanBuilder.Build(item, emulator, nativeWrappers: null, launchFilePath);

        Assert.Equal("/usr/bin/example-emulator", plan.FileName);
        Assert.Equal(
            "--dir \"/games/PlayStation One\" --name \"Game Disc.chd\" " +
            "--base \"Game Disc\" \"/games/PlayStation One/Game Disc.chd\" --fullscreen",
            plan.Arguments);
        Assert.False(plan.UseShellExecute);
    }

    [Fact]
    public void Build_FoldsWrappersInOuterToInnerOrder()
    {
        var item = new MediaItem("Wrapped game");
        var emulator = new EmulatorConfig
        {
            Path = "/usr/bin/example-emulator",
            Arguments = "{file} --fullscreen"
        };
        LaunchWrapper[] wrappers =
        [
            new() { Path = "/usr/bin/gamemoderun" },
            new() { Path = "/usr/bin/mangohud", Args = "--dlsym {file}" }
        ];

        var plan = LaunchPlanBuilder.Build(
            item,
            emulator,
            wrappers,
            "/games/Game One.rom");

        Assert.Equal("/usr/bin/gamemoderun", plan.FileName);
        Assert.Equal(
            "/usr/bin/mangohud --dlsym /usr/bin/example-emulator \"/games/Game One.rom\" --fullscreen",
            plan.Arguments);
        Assert.True(plan.UseShellExecute);
    }

    [Fact]
    public void Build_PreservesOuterWrapperPathContainingSpaces()
    {
        var item = new MediaItem("Wrapped game");
        LaunchWrapper[] wrappers =
        [
            new() { Path = "/opt/My Wrapper/wrapper.sh" }
        ];

        var plan = LaunchPlanBuilder.Build(
            item,
            inheritedConfig: null,
            wrappers,
            "/games/game.sh");

        Assert.Equal("/opt/My Wrapper/wrapper.sh", plan.FileName);
        Assert.Equal("/games/game.sh", plan.Arguments);
        Assert.Equal(!OperatingSystem.IsLinux(), plan.UseShellExecute);
    }

    [Fact]
    public void Build_RemovesFileMarkerFromDirectNativeArguments()
    {
        var item = new MediaItem("Native game")
        {
            LauncherArgs = "  --first   \"{file}\"   --second  "
        };

        var plan = LaunchPlanBuilder.Build(
            item,
            inheritedConfig: null,
            nativeWrappers: null,
            "/games/native-game");

        Assert.Equal("/games/native-game", plan.FileName);
        Assert.Equal("--first --second", plan.Arguments);
        Assert.Equal(!OperatingSystem.IsLinux(), plan.UseShellExecute);
    }

    [Fact]
    public void Build_RejectsDirectNativeLaunchWithoutAFile()
    {
        var item = new MediaItem("Missing native game");

        var error = Assert.Throws<InvalidOperationException>(() =>
            LaunchPlanBuilder.Build(
                item,
                inheritedConfig: null,
                nativeWrappers: null,
                launchFilePath: null));

        Assert.Contains("at least one valid file", error.Message);
    }
}
