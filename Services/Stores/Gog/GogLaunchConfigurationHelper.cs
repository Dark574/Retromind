using System.IO;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

internal static class GogLaunchConfigurationHelper
{
    public static string? ResolveRunnerExecutablePath(RunnerVersionKind kind, string? configuredPath)
        => RunnerVersionPathHelper.ResolveExecutablePath(kind, configuredPath);

    public static string? ResolvePreferredRunnerVersionId(
        MediaItem item,
        EmulatorConfig? inheritedEmulator)
    {
        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallRunnerVersionId, out var installedRunnerId) &&
            !string.IsNullOrWhiteSpace(installedRunnerId))
        {
            return installedRunnerId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(item.RunnerVersionId))
            return item.RunnerVersionId.Trim();

        return string.IsNullOrWhiteSpace(inheritedEmulator?.DefaultRunnerVersionId)
            ? null
            : inheritedEmulator.DefaultRunnerVersionId.Trim();
    }

    public static bool ShouldPreserveRunnerInheritance(
        MediaItem item,
        EmulatorConfig? inheritedEmulator,
        string? selectedRunnerVersionId) =>
        string.IsNullOrWhiteSpace(item.RunnerVersionId) &&
        !string.IsNullOrWhiteSpace(inheritedEmulator?.DefaultRunnerVersionId) &&
        string.Equals(
            inheritedEmulator.DefaultRunnerVersionId.Trim(),
            selectedRunnerVersionId?.Trim(),
            System.StringComparison.Ordinal);

    public static GogInstallPlatform? InferLegacyInstallPlatform(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) ||
            string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
            return GogInstallPlatform.Windows;

        var launchPath = item.GetPrimaryLaunchPath();
        var extension = Path.GetExtension(launchPath ?? string.Empty);
        if (extension is not null &&
            (extension.Equals(".exe", System.StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".msi", System.StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".bat", System.StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".cmd", System.StringComparison.OrdinalIgnoreCase)))
        {
            return GogInstallPlatform.Windows;
        }

        var launcher = item.LauncherPath?.Trim();
        if (!string.IsNullOrWhiteSpace(launcher) &&
            (launcher.Equals("umu-run", System.StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(launcher).StartsWith("wine", System.StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(launcher).Equals("proton", System.StringComparison.OrdinalIgnoreCase)))
        {
            return GogInstallPlatform.Windows;
        }

        return string.IsNullOrWhiteSpace(launchPath)
            ? null
            : GogInstallPlatform.Linux;
    }

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
