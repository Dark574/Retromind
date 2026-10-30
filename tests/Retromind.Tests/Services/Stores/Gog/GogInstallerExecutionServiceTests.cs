using System.Collections.Concurrent;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerExecutionServiceTests
{
    [Fact]
    public async Task RunLinuxAsync_InstallsThroughSafeTemporaryDestinationAndPreservesExecutableMode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("My Game (Test)");
        var stagingPath = temporaryDirectory.CreateDirectory("staging");
        var installerPath = CreateInstallerScript(
            stagingPath,
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
            stat -c '%a' "$destination" > "$destination/temporary-directory-mode.txt"
            printf 'game' > "$destination/game.x86_64"
            """);
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var output = new ConcurrentQueue<string>();
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("123", installPath, package, true, true),
            output.Enqueue);

        var installedExecutable = Path.Combine(installPath, "game.x86_64");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(installedExecutable));
        Assert.Contains(output, line => line.StartsWith("Installer destination (temporary):", StringComparison.Ordinal));
        Assert.Contains("Promoting install from temporary path", string.Join('\n', output));
        Assert.Equal(
            "700",
            File.ReadAllText(Path.Combine(installPath, "temporary-directory-mode.txt")).Trim());
        Assert.True((File.GetUnixFileMode(installedExecutable) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task RunLinuxAsync_ExtractFallbackUsesPrivateDirectoryAndDoesNotFollowCleanupLinks()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var externalDirectory = new TemporaryDirectory();
        var externalSentinel = externalDirectory.CreateFile("keep.txt", "keep");
        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("fallback-game");
        var stagingPath = temporaryDirectory.CreateDirectory("staging");
        var installerPath = CreateInstallerScript(
            stagingPath,
            $$"""
            #!/bin/sh
            extract=0
            target=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--noexec" ]; then
                extract=1
              elif [ "$1" = "--target" ]; then
                shift
                target="$1"
              fi
              shift
            done
            [ "$extract" -eq 1 ] || exit 1
            [ -n "$target" ] || exit 9
            ln -s "{{externalDirectory.RootPath}}" "$target/external-link"
            cat > "$target/startmojo.sh" <<'MOJO'
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
            pwd > "$destination/extraction-root.txt"
            stat -c '%a' . > "$destination/extraction-mode.txt"
            printf 'game' > "$destination/game.x86_64"
            MOJO
            """);
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var output = new ConcurrentQueue<string>();
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("fallback", installPath, package, false, true),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        var extractionRoot = File.ReadAllText(Path.Combine(installPath, "extraction-root.txt")).Trim();
        Assert.StartsWith("retromind-gog-extract-", Path.GetFileName(extractionRoot), StringComparison.Ordinal);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            Path.GetDirectoryName(extractionRoot));
        Assert.Equal("700", File.ReadAllText(Path.Combine(installPath, "extraction-mode.txt")).Trim());
        Assert.False(Directory.Exists(extractionRoot));
        Assert.True(File.Exists(externalSentinel));
        Assert.Contains("Fallback: extract mode", string.Join('\n', output));
    }

    [Fact]
    public async Task RunLinuxAsync_ShellSensitiveExistingDestinationUsesPrivateTemporaryAlias()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Alias Game");
        var stagingPath = temporaryDirectory.CreateDirectory("staging");
        var installerPath = CreateInstallerScript(
            stagingPath,
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
            alias_root="$(dirname "$destination")"
            printf '%s' "$alias_root" > "$destination/alias-root.txt"
            stat -c '%a' "$alias_root" > "$destination/alias-mode.txt"
            printf 'game' > "$destination/game.x86_64"
            """);
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("alias", installPath, package, false, true),
            _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        var aliasRoot = File.ReadAllText(Path.Combine(installPath, "alias-root.txt"));
        Assert.StartsWith("retromind-gog-target-", Path.GetFileName(aliasRoot), StringComparison.Ordinal);
        Assert.Equal("700", File.ReadAllText(Path.Combine(installPath, "alias-mode.txt")).Trim());
        Assert.False(Directory.Exists(aliasRoot));
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
    }

    [Fact]
    public async Task RunLinuxAsync_RejectsSuccessfulProcessWithoutChangedPayloadWhenRequired()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("unchanged-game");
        var stagingPath = temporaryDirectory.CreateDirectory("staging");
        var installerPath = CreateInstallerScript(stagingPath, "#!/bin/sh\nexit 0\n");
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("456", installPath, package, true, true),
            _ => { });

        Assert.False(result.Success);
        Assert.Contains("without changing game files", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWindowsAsync_PreparesRequiredDrivesBeforeWineAndReusesIncompletePrefix(bool incompletePrefix)
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("windows-game");
        var stagingPath = temporaryDirectory.CreateDirectory("windows-staging");
        var installerPath = Path.Combine(stagingPath, "setup_game_x64.exe");
        File.WriteAllText(installerPath, "installer");
        var prefixPath = Path.Combine(temporaryDirectory.RootPath, "Library", "Prefixes", "gog_789_Test_Game");
        if (incompletePrefix)
        {
            var dosdevices = Directory.CreateDirectory(Path.Combine(prefixPath, "dosdevices")).FullName;
            File.CreateSymbolicLink(Path.Combine(dosdevices, "r:"), "../../../Games");
            File.WriteAllText(Path.Combine(prefixPath, "keep.txt"), "existing prefix data");
        }
        var winePath = CreateExecutableScript(
            stagingPath,
            "fake-wine.sh",
            $"""
            #!/bin/sh
            printf 'C mapping before Wine: %s\n' "$(readlink "$WINEPREFIX/dosdevices/c:")"
            printf 'Z mapping before Wine: %s\n' "$(readlink "$WINEPREFIX/dosdevices/z:")"
            [ -d "$WINEPREFIX/dosdevices/c:" ] && echo 'C drive accessible before Wine'
            mkdir -p '{installPath}'
            printf 'installed' > '{Path.Combine(installPath, "game.exe")}'
            printf '%s\n' "$@"
            """);
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var output = new ConcurrentQueue<string>();
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunWindowsAsync(
            new GogWindowsInstallerExecutionRequest(
                "789",
                "Test: Game",
                incompletePrefix ? Path.Combine("Prefixes", "gog_789_Test_Game") : null,
                installPath,
                winePath,
                package,
                GogWindowsInstallerPreference.AutoPrefer64,
                true,
                true),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("C mapping before Wine: ../drive_c", output);
        Assert.Contains("Z mapping before Wine: /", output);
        Assert.Contains("C drive accessible before Wine", output);
        if (incompletePrefix)
            Assert.Equal("existing prefix data", File.ReadAllText(Path.Combine(prefixPath, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(installPath, "game.exe")));
        Assert.True(Directory.Exists(
            Path.Combine(temporaryDirectory.RootPath, "Library", "Prefixes", "gog_789_Test_Game")));
        Assert.NotNull(new DirectoryInfo(Path.Combine(
            temporaryDirectory.RootPath,
            "Library",
            "Prefixes",
            "gog_789_Test_Game",
            "dosdevices",
            "r:")).LinkTarget);
        Assert.Contains("/DIR=Z:\\", string.Join('\n', output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWindowsAsync_PreservesInvalidExistingMappingAndDoesNotStartWine(bool danglingLink)
    {
        using var temp = new TemporaryDirectory();
        var prefix = temp.CreateDirectory("Library", "Prefixes", "existing");
        var dosdevices = Directory.CreateDirectory(Path.Combine(prefix, "dosdevices")).FullName;
        var mapping = Path.Combine(dosdevices, "c:");
        if (danglingLink) File.CreateSymbolicLink(mapping, "../missing-drive");
        else File.WriteAllText(mapping, "keep this file");
        var staging = temp.CreateDirectory("staging");
        var installer = temp.CreateFile("staging/setup.exe");
        var started = temp.GetPath("wine-started");
        var wine = CreateExecutableScript(staging, "wine", $"#!/bin/sh\ntouch '{started}'\n");

        var result = await CreateService(temp.RootPath).RunWindowsAsync(
            new GogWindowsInstallerExecutionRequest("123", "Game", Path.Combine("Prefixes", "existing"),
                temp.GetPath("game"), wine, new GogDownloadedInstallerPackage(staging, installer, [installer]),
                GogWindowsInstallerPreference.AutoPrefer64, true, true), _ => { });

        Assert.False(result.Success);
        Assert.Contains("C: and Z:", result.ErrorMessage);
        Assert.False(File.Exists(started));
        if (danglingLink) Assert.Equal("../missing-drive", new FileInfo(mapping).LinkTarget);
        else Assert.Equal("keep this file", File.ReadAllText(mapping));
        Assert.True(File.Exists(installer));
    }

    [Fact]
    public async Task RunWindowsAsync_PreservesValidUserDriveMapping()
    {
        using var temp = new TemporaryDirectory();
        var prefix = temp.CreateDirectory("Library", "Prefixes", "existing");
        var userDrive = Directory.CreateDirectory(Path.Combine(prefix, "custom-drive")).FullName;
        var dosdevices = Directory.CreateDirectory(Path.Combine(prefix, "dosdevices")).FullName;
        File.CreateSymbolicLink(Path.Combine(dosdevices, "c:"), "../custom-drive");
        var staging = temp.CreateDirectory("staging");
        var installer = temp.CreateFile("staging/setup.exe");
        var install = temp.CreateDirectory("game");
        var wine = CreateExecutableScript(staging, "wine", $"#!/bin/sh\nprintf installed > '{Path.Combine(install, "game.exe")}'\n");

        var result = await CreateService(temp.RootPath).RunWindowsAsync(
            new GogWindowsInstallerExecutionRequest("123", "Game", prefix, install, wine,
                new GogDownloadedInstallerPackage(staging, installer, [installer]),
                GogWindowsInstallerPreference.AutoPrefer64, true, true), _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("../custom-drive", new FileInfo(Path.Combine(dosdevices, "c:")).LinkTarget);
        Assert.True(Directory.Exists(userDrive));
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, false)]
    [InlineData(true, 7, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 0, true)]
    public async Task RunWindowsAsync_CancellationUsesOnlySiblingWineserverAndSurvivesCleanupFailures(
        bool serverAvailable, int serverExitCode, bool cancelAfterProcessExit)
    {
        using var temp = new TemporaryDirectory();
        var staging = temp.CreateDirectory("staging");
        var installer = temp.CreateFile("staging/setup.exe");
        var stoppedPrefix = temp.GetPath("stopped-prefix.txt");
        var stoppedArgs = temp.GetPath("stop-args.txt");
        var wine = CreateExecutableScript(staging, "wine", "#!/bin/sh\necho installer-ready\n" +
            (cancelAfterProcessExit ? "exit 0\n" : "exec sleep 30\n"));
        if (serverAvailable) CreateExecutableScript(staging, "wineserver", $"""
            #!/bin/sh
            printf '%s' "$WINEPREFIX" > '{stoppedPrefix}'
            printf '%s' "$*" > '{stoppedArgs}'
            {(serverExitCode < 0 ? "exec sleep 30" : $"exit {serverExitCode}")}
            """);
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<string>();
        var execution = CreateService(temp.RootPath).RunWindowsAsync(
            new GogWindowsInstallerExecutionRequest("cancel", "Game", null, temp.GetPath("game"), wine,
                new GogDownloadedInstallerPackage(staging, installer, [installer]),
                GogWindowsInstallerPreference.AutoPrefer64, true, true), line =>
            {
                output.Enqueue(line);
                if (cancelAfterProcessExit
                        ? line.StartsWith("[Windows installer] Process outcome:", StringComparison.Ordinal)
                        : line == "installer-ready")
                    ready.TrySetResult();
            }, cancellation.Token);

        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(8)));
            if (serverAvailable)
            {
                Assert.Equal(temp.GetPath("Library", "Prefixes", "gog_cancel_Game"), File.ReadAllText(stoppedPrefix));
                Assert.Equal("-k", File.ReadAllText(stoppedArgs));
            }
            else
            {
                Assert.False(File.Exists(stoppedPrefix));
                Assert.Contains(output, line => line.Contains("Could not find wineserver", StringComparison.Ordinal));
            }
            Assert.Single(output, line => line.StartsWith("[Windows installer] Attempt", StringComparison.Ordinal));
            if (serverAvailable && serverExitCode > 0)
                Assert.Contains(output, line => line.Contains("Wine prefix shutdown failed", StringComparison.Ordinal));
            if (serverAvailable && serverExitCode < 0)
                Assert.Contains(output, line => line.Contains("Could not complete Wine prefix shutdown", StringComparison.Ordinal));
            Assert.True(File.Exists(installer));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public void BuildWindowsInstallerEntryCandidates_PutsRequestedArchitectureFirst()
    {
        var package = new GogDownloadedInstallerPackage(
            "/staging",
            "/staging/setup_game_x64.exe",
            ["/staging/setup_game_x64.exe", "/staging/setup_game_x86.exe", "/staging/data.bin"]);

        var prefer32 = GogInstallerExecutionService.BuildWindowsInstallerEntryCandidates(
            package,
            GogWindowsInstallerPreference.Prefer32);
        var prefer64 = GogInstallerExecutionService.BuildWindowsInstallerEntryCandidates(
            package,
            GogWindowsInstallerPreference.Prefer64);

        Assert.EndsWith("setup_game_x86.exe", prefer32[0], StringComparison.Ordinal);
        Assert.EndsWith("setup_game_x64.exe", prefer64[0], StringComparison.Ordinal);
        Assert.DoesNotContain(prefer64, path => path.EndsWith(".bin", StringComparison.Ordinal));
    }

    [Fact]
    public void ToWineWindowsAbsolutePath_MapsLinuxPathToWineZDrive()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Equal(
            @"Z:\mnt\games\My Game",
            GogInstallerExecutionService.ToWineWindowsAbsolutePath("/mnt/games/My Game"));
    }

    [Theory]
    [InlineData("/games/SimpleGame", false)]
    [InlineData("/games/My Game", true)]
    [InlineData("/games/Game (64-bit)", true)]
    [InlineData("/games/Game$HOME", true)]
    public void HasShellSensitivePathCharacters_ClassifiesInstallerDestinations(string path, bool expected)
    {
        Assert.Equal(expected, GogInstallerExecutionService.HasShellSensitivePathCharacters(path));
    }

    private static string CreateInstallerScript(string stagingPath, string script)
    {
        return CreateExecutableScript(stagingPath, "installer.sh", script);
    }

    private static string CreateExecutableScript(string directory, string fileName, string script)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static GogInstallerExecutionService CreateService(string rootPath)
        => new(new GogInstallerProcessService(), Path.Combine(rootPath, "Library"));
}
