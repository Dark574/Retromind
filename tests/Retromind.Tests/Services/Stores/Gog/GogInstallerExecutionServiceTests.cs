using System.Collections.Concurrent;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerExecutionServiceTests
{
    private readonly GogInstallerExecutionService _service = new(new GogInstallerProcessService());

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
            printf 'game' > "$destination/game.x86_64"
            """);
        var package = new GogDownloadedInstallerPackage(stagingPath, installerPath, [installerPath]);
        var output = new ConcurrentQueue<string>();

        var result = await _service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("123", installPath, package, true, true),
            output.Enqueue);

        var installedExecutable = Path.Combine(installPath, "game.x86_64");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(installedExecutable));
        Assert.Contains(output, line => line.StartsWith("Installer destination (temporary):", StringComparison.Ordinal));
        Assert.Contains("Promoting install from temporary path", string.Join('\n', output));
        Assert.True((File.GetUnixFileMode(installedExecutable) & UnixFileMode.UserExecute) != 0);
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

        var result = await _service.RunLinuxAsync(
            new GogLinuxInstallerExecutionRequest("456", installPath, package, true, true),
            _ => { });

        Assert.False(result.Success);
        Assert.Contains("without changing game files", result.ErrorMessage, StringComparison.Ordinal);
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
        var installerPath = Path.Combine(stagingPath, "installer.sh");
        File.WriteAllText(installerPath, script);
        return installerPath;
    }
}
