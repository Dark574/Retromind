using Retromind.Models;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogLaunchConfigurationHelperTests
{
    [Theory]
    [InlineData(GogInstallPlatform.Windows, MediaType.Emulator)]
    [InlineData(GogInstallPlatform.Linux, MediaType.Native)]
    public void SetInstalledMediaType_UsesPlatformAppropriateType(
        GogInstallPlatform platform,
        MediaType expectedMediaType)
    {
        var item = new MediaItem("GOG game")
        {
            MediaType = MediaType.Command,
            EmulatorId = "stale-emulator"
        };

        GogLaunchConfigurationHelper.SetInstalledMediaType(item, platform);

        Assert.Equal(expectedMediaType, item.MediaType);
        Assert.Null(item.EmulatorId);
    }

    [Fact]
    public void ClearAfterUninstall_ResetsRunnerLaunchConfiguration()
    {
        var item = new MediaItem("GOG game")
        {
            MediaType = MediaType.Emulator,
            EmulatorId = "emulator",
            LauncherPath = "umu-run",
            LauncherArgs = "{file}",
            RunnerVersionId = "proton-runner"
        };

        GogLaunchConfigurationHelper.ClearAfterUninstall(item);

        Assert.Equal(MediaType.Native, item.MediaType);
        Assert.Null(item.EmulatorId);
        Assert.Null(item.LauncherPath);
        Assert.Null(item.LauncherArgs);
        Assert.Null(item.RunnerVersionId);
    }
}
