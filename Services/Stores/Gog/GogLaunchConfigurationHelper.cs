using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

internal static class GogLaunchConfigurationHelper
{
    public static void SetInstalledMediaType(MediaItem item, GogInstallPlatform platform)
    {
        item.MediaType = platform == GogInstallPlatform.Windows
            ? MediaType.Emulator
            : MediaType.Native;
        item.EmulatorId = null;
    }

    public static void ClearAfterUninstall(MediaItem item)
    {
        item.MediaType = MediaType.Native;
        item.EmulatorId = null;
        item.LauncherPath = null;
        item.LauncherArgs = null;
        item.RunnerVersionId = null;
    }
}
