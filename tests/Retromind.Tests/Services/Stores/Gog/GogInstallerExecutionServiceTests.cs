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

    [Fact]
    public async Task RunWindowsAsync_UsesWinePrefixAndDetectsInstalledPayload()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("windows-game");
        var stagingPath = temporaryDirectory.CreateDirectory("windows-staging");
        var installerPath = Path.Combine(stagingPath, "setup_game_x64.exe");
        File.WriteAllText(installerPath, "installer");
        var winePath = CreateExecutableScript(
            stagingPath,
            "fake-wine.sh",
            $"""
            #!/bin/sh
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
                null,
                installPath,
                winePath,
                package,
                GogWindowsInstallerPreference.AutoPrefer64,
                true,
                true),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
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
