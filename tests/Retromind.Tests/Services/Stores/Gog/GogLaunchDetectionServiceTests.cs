using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogLaunchDetectionServiceTests
{
    private readonly GogLaunchDetectionService _service = new();

    [Fact]
    public void DetectFromLocalMetadata_PrefersMatchingGameAndPreservesLaunchData()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("game-install");
        var matchingExecutable = CreateFile(installRoot, "bin/game.x86_64");
        var otherExecutable = CreateFile(installRoot, "other.x86_64");
        WriteInfoFile(
            installRoot,
            "goggame-other.info",
            "999",
            "other.x86_64",
            null,
            null);
        WriteInfoFile(
            installRoot,
            "goggame-matching.info",
            "123",
            "bin/game.x86_64",
            "--fullscreen",
            "bin");

        var result = _service.DetectFromLocalMetadata(
            installRoot,
            "123",
            GogInstallPlatform.Linux);

        Assert.NotNull(result);
        Assert.Equal(matchingExecutable, result.ExecutablePath);
        Assert.NotEqual(otherExecutable, result.ExecutablePath);
        Assert.Equal("--fullscreen", result.LaunchArguments);
        Assert.Equal(Path.Combine(installRoot, "bin"), result.WorkingDirectory);
        Assert.Equal(installRoot, result.InstallRoot);
    }

    [Fact]
    public void DetectFromPlayTasks_ResolvesWindowsMetadataCaseInsensitively()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("windows-game");
        var executable = CreateFile(installRoot, "Bin/MyGame.EXE");

        var result = _service.DetectFromPlayTasks(
            installRoot,
            GogInstallPlatform.Windows,
            [new GogPlayTaskInfo("bin\\mygame.exe", "-windowed", "BIN", true)]);

        Assert.NotNull(result);
        Assert.Equal(executable, result.ExecutablePath);
        Assert.Equal("-windowed", result.LaunchArguments);
        Assert.Equal(Path.Combine(installRoot, "Bin"), result.WorkingDirectory);
    }

    [Fact]
    public void DetectFromPlayTasks_RejectsPathsOutsideInstallRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("contained-game");
        var outsideExecutable = temporaryDirectory.CreateFile("outside.exe");

        var relativeEscape = _service.DetectFromPlayTasks(
            installRoot,
            GogInstallPlatform.Windows,
            [new GogPlayTaskInfo("../outside.exe", null, null, true)]);
        var absoluteEscape = _service.DetectFromPlayTasks(
            installRoot,
            GogInstallPlatform.Windows,
            [new GogPlayTaskInfo(outsideExecutable, null, null, true)]);

        Assert.Null(relativeEscape);
        Assert.Null(absoluteEscape);
    }

    [Fact]
    public void DetectFromPlayTasks_UsesExecutableDirectoryForEscapingWorkingDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("working-directory-game");
        var executable = CreateFile(installRoot, "bin/game.exe");

        var result = _service.DetectFromPlayTasks(
            installRoot,
            GogInstallPlatform.Windows,
            [new GogPlayTaskInfo("bin/game.exe", null, "../outside", true)]);

        Assert.NotNull(result);
        Assert.Equal(Path.GetDirectoryName(executable), result.WorkingDirectory);
    }

    [Fact]
    public void DetectFromFilesystem_IgnoresInstallerStagingAndSetupExecutables()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("filesystem-game");
        CreateFile(installRoot, ".retromind-gog-installers/setup/My Game.exe");
        CreateFile(installRoot, "support/setup.exe");
        var expected = CreateFile(installRoot, "game/My Game.exe");

        var result = _service.DetectFromFilesystem(
            "My Game",
            installRoot,
            GogInstallPlatform.Windows);

        Assert.NotNull(result);
        Assert.Equal(expected, result.ExecutablePath);
    }

    [Fact]
    public void PreferLinuxStartScript_ReplacesMetadataBinaryWithInstallerLauncher()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installRoot = temporaryDirectory.CreateDirectory("linux-game");
        var binary = CreateFile(installRoot, "game/game.x86_64");
        var startScript = CreateFile(installRoot, "start.sh");
        var metadataResult = new GogDetectedLaunchInfo(
            binary,
            "--from-metadata",
            Path.GetDirectoryName(binary),
            installRoot);

        var result = _service.PreferLinuxStartScript(metadataResult, installRoot);

        Assert.Equal(startScript, result.ExecutablePath);
        Assert.Null(result.LaunchArguments);
        Assert.Equal(installRoot, result.WorkingDirectory);
    }

    private static string CreateFile(string installRoot, string relativePath)
    {
        var path = Path.Combine(installRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    private static void WriteInfoFile(
        string installRoot,
        string fileName,
        string rootGameId,
        string executablePath,
        string? arguments,
        string? workingDirectory)
    {
        var argumentsJson = arguments == null ? "null" : $"\"{arguments}\"";
        var workingDirectoryJson = workingDirectory == null ? "null" : $"\"{workingDirectory}\"";
        File.WriteAllText(
            Path.Combine(installRoot, fileName),
            $$"""
            {
              "rootGameId": "{{rootGameId}}",
              "playTasks": [
                {
                  "path": "{{executablePath}}",
                  "arguments": {{argumentsJson}},
                  "workingDir": {{workingDirectoryJson}},
                  "isPrimary": true
                }
              ]
            }
            """);
    }
}
