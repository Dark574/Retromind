using System.Collections.Concurrent;
using Retromind.Services.Stores.Gog;

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
}
