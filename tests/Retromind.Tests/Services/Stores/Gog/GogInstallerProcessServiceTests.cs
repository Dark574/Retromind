using System.Collections.Concurrent;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerProcessServiceTests
{
    private readonly GogInstallerProcessService _service = new();

    [Fact]
    public async Task ExecuteAsync_CapturesFinalOutputAndNonZeroExitCode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var output = new ConcurrentQueue<string>();
        var startInfo = _service.CreateStartInfo(Path.GetTempPath());
        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("printf 'first-line\\n'; printf 'final-error\\n' >&2; exit 7");

        var result = await _service.ExecuteAsync(startInfo, output.Enqueue);

        Assert.True(result.Started);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("first-line", output);
        Assert.Contains("final-error", output);
        Assert.Contains("Exit code: 7", output);
    }

    [Fact]
    public void FormatCommand_QuotesArgumentsWithoutChangingTheirContents()
    {
        var startInfo = _service.CreateStartInfo(Path.GetTempPath());
        startInfo.FileName = "installer";
        startInfo.ArgumentList.Add("--destination");
        startInfo.ArgumentList.Add("/tmp/My Game");

        var command = GogInstallerProcessService.FormatCommand(startInfo);

        Assert.Equal("installer --destination \"/tmp/My Game\"", command);
    }

    [Theory]
    [InlineData("unknown option --bad")]
    [InlineData("invalid option: --bad")]
    [InlineData("unrecognized flag --bad")]
    public void UnsupportedFlagDetection_RecognizesInstallerDiagnostics(string line)
    {
        Assert.True(GogInstallerProcessService.LooksLikeUnsupportedFlagError(line));
    }

    [Theory]
    [InlineData("syntax error near unexpected token")]
    [InlineData("Syntaxfehler beim unerwarteten Symbol")]
    public void ShellParsingDetection_RecognizesLocalizedDiagnostics(string line)
    {
        Assert.True(GogInstallerProcessService.LooksLikeShellArgumentParsingError(line));
    }

    [Theory]
    [InlineData("konsole: unknown option -title")]
    [InlineData("couldn't run mojosetup in terminal")]
    [InlineData("xterm invalid option")]
    public void TerminalDetection_RecognizesKnownMojoSetupFailures(string line)
    {
        Assert.True(GogInstallerProcessService.LooksLikeTerminalSpawnError(line));
    }

    [Theory]
    [InlineData("Unhandled exception code c0000005")]
    [InlineData("EXCEPTION_ACCESS_VIOLATION")]
    [InlineData("NtRaiseException")]
    public void RuntimeCrashDetection_RecognizesWineCrashOutput(string line)
    {
        Assert.True(GogInstallerProcessService.LooksLikeRuntimeCrashError(line));
    }

    [Fact]
    public void PrepareLinuxCompatibilityEnvironment_UsesPrivateTemporaryDirectoryAndCleansIt()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var fakeKonsolePath = temporaryDirectory.CreateFile("konsole", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            fakeKonsolePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var testPath = string.IsNullOrEmpty(originalPath)
            ? temporaryDirectory.RootPath
            : temporaryDirectory.RootPath + Path.PathSeparator + originalPath;
        using var environment = new EnvironmentVariableScope(("PATH", testPath));

        var compatibilityEnvironment = _service.PrepareLinuxCompatibilityEnvironment(_ => { });
        var shimDirectory = Assert.IsType<string>(compatibilityEnvironment.ShimDirectory);
        try
        {
            const UnixFileMode accessMask =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            Assert.StartsWith("retromind-gog-shim-", Path.GetFileName(shimDirectory), StringComparison.Ordinal);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                Path.GetDirectoryName(shimDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(shimDirectory) & accessMask);
        }
        finally
        {
            GogInstallerProcessService.CleanupLinuxCompatibilityEnvironment(compatibilityEnvironment);
        }

        Assert.False(Directory.Exists(shimDirectory));
    }
}
