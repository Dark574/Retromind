using Retromind.Models;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogLaunchConfigurationHelperTests
{
    [Fact]
    public void ResolveRunnerExecutablePath_ResolvesProtonDirectory()
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory("GE-Proton");
        var protonExecutable = temp.CreateFile(Path.Combine("GE-Proton", "proton"));

        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Proton,
            runnerDirectory);

        Assert.Equal(protonExecutable, resolved);
    }

    [Fact]
    public void ResolveRunnerExecutablePath_ResolvesWineDirectory()
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory("Wine");
        var wineExecutable = temp.CreateFile(Path.Combine("Wine", "bin", "wine"));

        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Wine,
            runnerDirectory);

        Assert.Equal(wineExecutable, resolved);
    }

    [Fact]
    public void ResolveRunnerExecutablePath_AcceptsDirectWineExecutable()
    {
        using var temp = new TemporaryDirectory();
        var wineExecutable = temp.CreateFile("wine");

        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Wine,
            wineExecutable);

        Assert.Equal(wineExecutable, resolved);
    }

    [Fact]
    public void ResolveRunnerExecutablePath_ResolvesPortableRelativePath()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var relativeRunnerPath = Path.Combine("Emulators", "ProtonVersions", "GE-Proton");
        temp.CreateDirectory(relativeRunnerPath);
        var protonExecutable = temp.CreateFile(Path.Combine(relativeRunnerPath, "proton"));

        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Proton,
            relativeRunnerPath);

        Assert.Equal(protonExecutable, resolved);
    }

    [Fact]
    public void ResolveRunnerExecutablePath_RejectsProtonExecutableAsConfiguredPath()
    {
        using var temp = new TemporaryDirectory();
        var protonExecutable = temp.CreateFile("proton");

        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Proton,
            protonExecutable);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-runner")]
    [InlineData("invalid\0path")]
    public void ResolveRunnerExecutablePath_RejectsUnavailableOrInvalidPath(string configuredPath)
    {
        var resolved = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
            RunnerVersionKind.Proton,
            configuredPath);

        Assert.Null(resolved);
    }

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
