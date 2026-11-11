using System.Diagnostics;

namespace Retromind.Tests.TestInfrastructure;

/// <summary>Runs the real packaging scripts with isolated files and command doubles, never real downloads or containers.</summary>
internal sealed class AppImageScriptFixture : IDisposable
{
    private const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public AppImageScriptFixture()
    {
        BinPath = Files.CreateDirectory("bin");
        LogPath = Files.GetPath("commands.log");
        Environment = new ProcessStartInfo().Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        // Host build secrets and runner defaults must not implicitly select a test case.
        foreach (var name in Environment.Keys.Where(name => name.StartsWith("RETROMIND_", StringComparison.Ordinal)).ToArray())
            Environment.Remove(name);
        Environment.Remove("DEBIAN_FRONTEND");
        Environment["PATH"] = BinPath + Path.PathSeparator + Environment.GetValueOrDefault("PATH");
        Environment["RETROMIND_TEST_LOG"] = LogPath;

        // Individual tests replace these guards with the command doubles they need.
        foreach (var command in new[] { "curl", "docker", "apt-get", "dpkg", "dpkg-query", "git" })
            Executable(command, "echo 'Unexpected external command' >&2\nexit 99\n");
    }

    public TemporaryDirectory Files { get; } = new();
    public string BinPath { get; }
    public string LogPath { get; }
    public Dictionary<string, string?> Environment { get; }

    public static string ScriptPath(string name) => Path.Combine(AppContext.BaseDirectory, "AppImageBuildFixtures", name);

    public void CopyBuildFiles(string destination, params string[] names)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in names)
        {
            var target = Path.Combine(destination, name);
            File.Copy(ScriptPath(name), target);
            if (name.EndsWith(".sh", StringComparison.Ordinal))
                File.SetUnixFileMode(target, ExecutableMode);
        }
    }

    public void Executable(string name, string body)
    {
        var path = Path.Combine(BinPath, name);
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
        File.SetUnixFileMode(path, ExecutableMode);
    }

    public string[] Commands() => File.ReadAllLines(LogPath);

    public string[] TemporaryDownloads() => Directory.GetFiles(Files.RootPath, "*.tmp.*", SearchOption.AllDirectories);

    public ScriptProcess Start(string script, params string[] arguments) => new(script, arguments, Environment, Files.RootPath);

    public async Task<ScriptResult> RunAsync(string script, params string[] arguments)
    {
        using var process = Start(script, arguments);
        return await process.CompleteAsync();
    }

    public void Dispose() => Files.Dispose();

    internal sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0,
            $"Script exited with {ExitCode}.\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}");
    }

    internal sealed class ScriptProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        public ScriptProcess(string script, string[] arguments, Dictionary<string, string?> environment, string workingDirectory)
        {
            // A separate process group lets the interruption test signal both sh and its blocked curl child.
            var startInfo = new ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add(script);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment.Clear();
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start AppImage script fixture.");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        public async Task<ScriptResult> CompleteAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token);
            return new ScriptResult(_process.ExitCode,
                await _stdout.WaitAsync(timeout.Token), await _stderr.WaitAsync(timeout.Token));
        }

        public async Task TerminateAsync()
        {
            var startInfo = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-TERM");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("-" + _process.Id);
            using var signal = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not signal script fixture.");
            await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, signal.ExitCode);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
            _process.Dispose();
        }
    }
}
