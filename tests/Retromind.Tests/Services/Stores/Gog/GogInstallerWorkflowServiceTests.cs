using System.Collections.Concurrent;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerWorkflowServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_ReusesDownloadAndHandlesSelectedArchive(bool keepInstaller, bool archiveBlocked)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temporaryDirectory.GetPath("Retromind.AppImage")));
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
        var settings = new DownloadSettings
        {
            KeepGogInstallers = keepInstaller,
            GogArchiveDirectory = archiveBlocked ? temporaryDirectory.GetPath("blocked-archive") : null
        };
        if (settings.GogArchiveDirectory is { } blockedPath)
            File.WriteAllText(blockedPath, "keep");

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
                RequireLinuxPayloadChange: true,
                DownloadSettings: settings),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.DownloadedPackage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.True(File.Exists(Path.Combine(stagingPath, "retromind-install.log")));
        Assert.Contains(output, line => line.Contains("Validating 1/1", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("Detailed log file:", StringComparison.Ordinal));
        Assert.Equal(archiveBlocked, result.ArchivePending);
        if (archiveBlocked)
        {
            Assert.True(File.Exists(installerPath));
            Assert.Equal("keep", File.ReadAllText(settings.GogArchiveDirectory!));
            return;
        }

        var archive = GogInstallerArchive.GetPackageDirectory(settings, package);
        var archivedFile = Path.Combine(archive, Path.GetFileName(installerPath));
        Assert.Equal(keepInstaller, File.Exists(archivedFile));
        if (keepInstaller)
        {
            Assert.Equal(File.ReadAllText(installerPath), File.ReadAllText(archivedFile));
            GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(result.DownloadedPackage.StagingDirectory);
            Assert.False(Directory.Exists(stagingPath));
            Assert.True(File.Exists(archivedFile));
            Assert.False(File.Exists(Path.Combine(archive, "retromind-install.log")));
        }
    }

    [Fact]
    public async Task RunAsync_CleanInstallPreservesNestedStagingAndRemovesOldPayload()
    {
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

    [Fact]
    public void DeleteStagingDirectoryBestEffort_OnLinuxRejectsDifferentlyCasedMarker()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stagingPath = temporaryDirectory.CreateDirectory(
            "game",
            ".RETROMIND-GOG-INSTALLERS",
            "123");

        GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(stagingPath);

        Assert.True(Directory.Exists(stagingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ManualUpdateAndCleanReinstallReuseTheQueuedInstaller(bool cleanInstall)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var installPath = temp.CreateDirectory("Library", "Games", "Queued Game");
        var source = CreateInstallerScript(temp.CreateDirectory("source"));
        var package = new GogInstallerPackage("123", GogInstallPlatform.Linux, "Queued Game", "2.0",
            [new GogInstallerDownloadFile("https://invalid.example/installer.sh", "installer.sh", new FileInfo(source).Length)]);
        var cache = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        File.Copy(source, Path.Combine(cache, "installer.sh"));
        var staging = Path.Combine(installPath, ".retromind-gog-installers", "123", "linux");
        var oldFile = Path.Combine(installPath, "old-game.bin");
        File.WriteAllText(oldFile, "old");
        using var client = new HttpClient(new RejectDownloadsHandler());
        var service = CreateService(temp.RootPath, client);
        var item = new MediaItem("Queued Game") { Id = "queued-game" };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "123";
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        var result = await service.RunAsync(new GogInstallerWorkflowRequest(
                item, "123", installPath,
                GogInstallPlatform.Linux, package, staging, GogWindowsInstallerPreference.AutoPrefer64,
                false, false, cleanInstall, UseTemporaryLinuxDestination: true, RequireLinuxPayloadChange: true),
            _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.Equal(!cleanInstall, File.Exists(oldFile));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.DownloadedPackage!.EntryFilePath));
    }

    private sealed class RejectDownloadsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("The queued installer must be reused without network transfers.");
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

    private static GogInstallerWorkflowService CreateService(string rootPath, HttpClient? downloads = null)
    {
        var installService = new GogInstallService(
            new GogAuthService(
                new InMemorySecretStore(),
                new GogOAuthClient(new HttpClient()),
                new GogPkceService(), new RecordingErrorService()),
            downloads ?? new HttpClient());
        var executionService = new GogInstallerExecutionService(
            new GogInstallerProcessService(),
            Path.Combine(rootPath, "Library"));
        return new GogInstallerWorkflowService(
            installService,
            executionService,
            new ProtonPrefixRelocationService(
                Path.Combine(rootPath, "Library"),
                new AppSettings()));
    }
}
