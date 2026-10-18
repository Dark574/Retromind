using System.Text.Json;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class CometProcessRunnerTests
{
    [Fact]
    public void Credentials_ToString_DoesNotExposeValues()
    {
        var credentials = CreateCredentials();

        var description = credentials.ToString();

        Assert.DoesNotContain(credentials.AccessToken, description, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.RefreshToken, description, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.UserId, description, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.Username, description, StringComparison.Ordinal);
        Assert.Contains("<redacted>", description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefaultDataDirectory_KeepsRuntimeStateInsideTools()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "retromind-root");

        var result = CometProcessRunner.GetDefaultDataDirectory(dataRoot);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(dataRoot), "Tools", "Comet", "Data"),
            result);
    }

    [Fact]
    public async Task StartAsync_UsesStdinAndIsolatedRuntimePaths()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var executable = CreateExecutable(
            temp.RootPath,
            """
            #!/bin/sh
            capture_root="$(dirname "$XDG_DATA_HOME")"
            printf '%s\n' "$@" > "$capture_root/arguments.txt"
            printf '%s\n%s\n%s\n%s\n' \
              "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME" "$COMET_IDLE_WAIT" \
              > "$capture_root/environment.txt"
            cat > "$capture_root/credentials.json"
            printf 'Listening on port 9977\n' >&2
            while :; do sleep 1; done
            """);
        var dataDirectory = Path.Combine(temp.RootPath, "Tools", "Comet", "Data");
        var runner = CreateRunner(executable, dataDirectory);
        var credentials = CreateCredentials();

        var session = await runner.StartAsync(credentials);
        var processId = session.ProcessId;
        try
        {
            Assert.False(session.HasExited);
            Assert.Equal(
                ["--credentials-stdin", "--quit"],
                await File.ReadAllLinesAsync(Path.Combine(dataDirectory, "arguments.txt")));
            Assert.Equal(
                [
                    Path.Combine(dataDirectory, "config"),
                    Path.Combine(dataDirectory, "data"),
                    Path.Combine(dataDirectory, "cache"),
                    "5"
                ],
                await File.ReadAllLinesAsync(Path.Combine(dataDirectory, "environment.txt")));

            using var json = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(dataDirectory, "credentials.json")));
            Assert.Equal(credentials.AccessToken, json.RootElement.GetProperty("access_token").GetString());
            Assert.Equal(credentials.RefreshToken, json.RootElement.GetProperty("refresh_token").GetString());
            Assert.Equal(credentials.UserId, json.RootElement.GetProperty("user_id").GetString());
            Assert.Equal(credentials.Username, json.RootElement.GetProperty("username").GetString());

            var visibleProcessData = string.Join(
                '\n',
                (await File.ReadAllLinesAsync(Path.Combine(dataDirectory, "arguments.txt")))
                .Concat(await File.ReadAllLinesAsync(Path.Combine(dataDirectory, "environment.txt"))));
            Assert.DoesNotContain(credentials.AccessToken, visibleProcessData, StringComparison.Ordinal);
            Assert.DoesNotContain(credentials.RefreshToken, visibleProcessData, StringComparison.Ordinal);
        }
        finally
        {
            await session.DisposeAsync();
        }

        AssertProcessExited(processId);
    }

    [Fact]
    public async Task StartAsync_DoesNotExposeCredentialsWhenProcessFails()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var executable = CreateExecutable(
            temp.RootPath,
            """
            #!/bin/sh
            cat >&2
            exit 7
            """);
        var runner = CreateRunner(
            executable,
            Path.Combine(temp.RootPath, "data"));
        var credentials = CreateCredentials();

        var exception = await Assert.ThrowsAsync<CometProcessException>(
            () => runner.StartAsync(credentials));

        Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.AccessToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.RefreshToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RejectsOversizedCredentialsBeforeStartingProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var markerPath = Path.Combine(temp.RootPath, "started");
        var executable = CreateExecutable(
            temp.RootPath,
            $"#!/bin/sh\ntouch '{markerPath}'\n");
        var runner = CreateRunner(executable, Path.Combine(temp.RootPath, "data"));
        var credentials = CreateCredentials() with
        {
            AccessToken = new string('a', 17 * 1024)
        };

        var exception = await Assert.ThrowsAsync<CometProcessException>(
            () => runner.StartAsync(credentials));

        Assert.Contains("16384-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task StartAsync_KillsProcessWhenReadinessTimesOut()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var executable = CreateExecutable(
            temp.RootPath,
            """
            #!/bin/sh
            capture_root="$(dirname "$XDG_DATA_HOME")"
            cat > /dev/null
            printf '%s' "$$" > "$capture_root/process-id.txt"
            while :; do sleep 1; done
            """);
        var dataDirectory = Path.Combine(temp.RootPath, "data");
        var resolver = new CometExecutableResolver(temp.RootPath, appDir: null);
        var runner = new CometProcessRunner(
            resolver,
            dataDirectory,
            TimeSpan.FromMilliseconds(200));

        var exception = await Assert.ThrowsAsync<CometProcessException>(
            () => runner.StartAsync(CreateCredentials()));

        Assert.Contains("did not become ready", exception.Message, StringComparison.Ordinal);
        var processId = int.Parse(
            await File.ReadAllTextAsync(Path.Combine(dataDirectory, "process-id.txt")));
        AssertProcessExited(processId);
    }

    private static CometCredentials CreateCredentials() =>
        new("access-secret", "refresh-secret", "123456", "player-name");

    private static CometProcessRunner CreateRunner(string executable, string dataDirectory)
    {
        var resolver = new CometExecutableResolver(
            Path.GetDirectoryName(executable)!,
            appDir: null);
        return new CometProcessRunner(resolver, dataDirectory, TimeSpan.FromSeconds(5));
    }

    private static string CreateExecutable(string directory, string contents)
    {
        var path = Path.Combine(directory, CometExecutableResolver.ExecutableName);
        File.WriteAllText(path, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // The process no longer exists.
        }
    }
}
