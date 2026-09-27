using System.Collections.Concurrent;
using Retromind.Models;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerWorkflowServiceTests
{
    [Fact]
    public async Task RunAsync_ReusesDownloadExecutesInstallerAndWritesDetailedLog()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Workflow Game");
        var stagingPath = temporaryDirectory.CreateDirectory(
            "Library",
            "Games",
            "Workflow Game",
            ".retromind-gog-installers",
            "123",
            "linux");
        var installerPath = CreateInstallerScript(stagingPath);
        var package = new GogInstallerPackage(
            "123",
            GogInstallPlatform.Linux,
            "Workflow installer",
            "1.0",
            [new GogInstallerDownloadFile(
                "https://invalid.example/installer.sh",
                Path.GetFileName(installerPath),
                new FileInfo(installerPath).Length)]);
        var item = new MediaItem { Id = "workflow-item", Title = "Workflow Game" };
        var output = new ConcurrentQueue<string>();
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "123",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: false,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.DownloadedPackage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.True(File.Exists(Path.Combine(stagingPath, "retromind-install.log")));
        Assert.Contains(output, line => line.Contains("Reusing 1/1", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("Detailed log file:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_CleanInstallPreservesNestedStagingAndRemovesOldPayload()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Clean Workflow Game");
        var stagingPath = temporaryDirectory.CreateDirectory(
            "Library",
            "Games",
            "Clean Workflow Game",
            ".retromind-gog-installers",
            "456",
            "linux");
        var oldPayloadPath = Path.Combine(installPath, "old-payload.bin");
        File.WriteAllText(oldPayloadPath, "old");
        var installerPath = CreateInstallerScript(stagingPath);
        var package = new GogInstallerPackage(
            "456",
            GogInstallPlatform.Linux,
            "Clean workflow installer",
            "2.0",
            [new GogInstallerDownloadFile(
                "https://invalid.example/installer.sh",
                Path.GetFileName(installerPath),
                new FileInfo(installerPath).Length)]);
        var item = new MediaItem { Id = "clean-workflow-item", Title = "Clean Workflow Game" };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "456";
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "456",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: true,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true),
            _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(oldPayloadPath));
        Assert.True(File.Exists(installerPath));
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
    }

    [Fact]
    public async Task RunAsync_CancelledDownloadReturnsCancelledStage()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Cancelled Workflow Game");
        var stagingPath = Path.Combine(installPath, ".retromind-gog-installers", "789", "linux");
        var package = new GogInstallerPackage(
            "789",
            GogInstallPlatform.Linux,
            "Cancelled workflow installer",
            "1.0",
            [new GogInstallerDownloadFile("https://invalid.example/installer.sh", "installer.sh", 10)]);
        var item = new MediaItem { Id = "cancelled-workflow-item", Title = "Cancelled Workflow Game" };
        var service = CreateService(temporaryDirectory.RootPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "789",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: false,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true),
            _ => { },
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(GogInstallerWorkflowFailureStage.Cancelled, result.FailureStage);
    }

    private static string CreateInstallerScript(string stagingPath)
    {
        var path = Path.Combine(stagingPath, "installer.sh");
        File.WriteAllText(
            path,
            """
            #!/bin/sh
            destination=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--destination" ]; then
                shift
                destination="$1"
              fi
              shift
            done
            [ -n "$destination" ] || exit 9
            mkdir -p "$destination"
            printf 'game' > "$destination/game.x86_64"
            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static GogInstallerWorkflowService CreateService(string rootPath)
    {
        var installService = new GogInstallService(
            new GogAuthService(
                new InMemorySecretStore(),
                new GogOAuthClient(new HttpClient()),
                new GogPkceService()),
            new HttpClient());
        var executionService = new GogInstallerExecutionService(
            new GogInstallerProcessService(),
            Path.Combine(rootPath, "Library"));
        return new GogInstallerWorkflowService(installService, executionService);
    }
}
