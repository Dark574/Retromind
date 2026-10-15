using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class LegendaryCliProcessRunnerTests
{
    [Fact]
    public void Command_ToString_DoesNotExposeStandardInput()
    {
        var command = new LegendaryCliCommand(
            "Epic sign-in",
            ["auth", "--disable-webview"],
            TimeSpan.FromMinutes(2),
            StandardInput: "secret-input");

        var description = command.ToString();

        Assert.DoesNotContain("secret-input", description, StringComparison.Ordinal);
        Assert.Contains("<redacted>", description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefaultConfigDirectory_KeepsDataBesideManagedTool()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "retromind-root");

        var result = LegendaryCliProcessRunner.GetDefaultConfigDirectory(dataRoot);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(dataRoot), "Tools", "Legendary", "Data"),
            result);
    }

    [Fact]
    public async Task RunAsync_UsesIsolatedConfigAndPreservesArgumentBoundaries()
    {
        using var temp = new TempDirectory();
        var executablePath = LegendaryExecutableResolver.GetManagedExecutablePath(temp.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        await File.WriteAllTextAsync(
            executablePath,
            "#!/bin/sh\nprintf '%s\\n' \"$LEGENDARY_CONFIG_PATH\"\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var configDirectory = Path.Combine(temp.RootPath, "config with spaces");
        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            pathEnvironment: null);
        var runner = new LegendaryCliProcessRunner(resolver, configDirectory);

        var result = await runner.RunAsync(
            new LegendaryCliCommand(
                "test",
                ["argument with spaces", "; touch should-not-exist"],
                TimeSpan.FromSeconds(5)));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                Path.GetFullPath(configDirectory),
                "argument with spaces",
                "; touch should-not-exist"
            ],
            result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(Directory.Exists(configDirectory));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "should-not-exist")));
    }

    [Fact]
    public async Task RunAsync_StreamsCarriageReturnAndNewlineSeparatedProgress()
    {
        using var temp = new TempDirectory();
        var executablePath = LegendaryExecutableResolver.GetManagedExecutablePath(temp.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        await File.WriteAllTextAsync(
            executablePath,
            "#!/bin/sh\nprintf 'Downloading 10%%\\rDownloading 20%%\\nFinished\\n' >&2\n");
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            pathEnvironment: null);
        var runner = new LegendaryCliProcessRunner(
            resolver,
            Path.Combine(temp.RootPath, "config"));
        var progress = new List<string>();

        var result = await runner.RunAsync(
            new LegendaryCliCommand(
                "test",
                [],
                Timeout.InfiniteTimeSpan,
                progress.Add));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["Downloading 10%", "Downloading 20%", "Finished"],
            progress);
    }

    [Fact]
    public async Task RunAsync_WritesSensitiveInputThroughStandardInput()
    {
        using var temp = new TempDirectory();
        var executablePath = LegendaryExecutableResolver.GetManagedExecutablePath(temp.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        await File.WriteAllTextAsync(
            executablePath,
            "#!/bin/sh\nIFS= read -r value\nprintf 'input=%s\\nbrowser=%s\\n' \"$value\" \"$BROWSER\"\n");
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            pathEnvironment: null);
        var runner = new LegendaryCliProcessRunner(
            resolver,
            Path.Combine(temp.RootPath, "config"));

        var result = await runner.RunAsync(
            new LegendaryCliCommand(
                "test",
                ["visible-argument"],
                TimeSpan.FromSeconds(5),
                StandardInput: "secret-input",
                SuppressBrowserLaunch: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["input=secret-input", "browser=true"],
            result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "retromind-legendary-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        internal string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
