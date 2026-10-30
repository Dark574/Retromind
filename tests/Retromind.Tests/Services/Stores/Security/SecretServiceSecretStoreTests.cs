using System.Diagnostics;
using Retromind.Services.Stores.Security;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Security;

public sealed class SecretServiceSecretStoreTests
{
    private static readonly SecretKey Key = new("private-service", "private-account");

    [Fact]
    public async Task SuccessfulCommand_ReturnsPersistentWriteAndNormalizesLookup()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var tool = CreateTool(temp, "case \"$1\" in\nstore) /bin/cat >/dev/null;;\nlookup) printf 'secret-value\\n';;\nclear) exit 0;;\nesac");
        ISecretStore store = new SecretServiceSecretStore(tool);

        Assert.Equal(SecretStorageStatus.Persistent, await store.SetWithStatusAsync(Key, "secret-value"));
        Assert.Equal("secret-value", await store.GetAsync(Key));
        await store.DeleteAsync(Key);
    }

    [Fact]
    public async Task MissingExecutable_ReadWriteAndDeleteAllFailWithoutFalseSuccess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var store = new SecretServiceSecretStore(temp.GetPath("missing-tool"));

        await Assert.ThrowsAsync<SecretServiceException>(() => store.SetAsync(Key, "secret"));
        await Assert.ThrowsAsync<SecretServiceException>(() => store.GetAsync(Key));
        await Assert.ThrowsAsync<SecretServiceException>(() => store.DeleteAsync(Key));
        Assert.Equal(SecretStorageStatus.Failed, await ((ISecretStore)store).SetWithStatusAsync(Key, "secret"));
        Assert.Equal(SecretStorageStatus.Failed, store.GetStorageStatus(Key));
    }

    [Theory]
    [InlineData("set")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task FailedCommand_DoesNotExposeStdoutStderrKeysOrValues(string operation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var tool = CreateTool(temp, "/bin/cat >/dev/null </dev/null\nprintf 'unlabeled-secret'\nprintf 'private-service private-account private-password' >&2\nexit 7");
        var store = new SecretServiceSecretStore(tool);

        var exception = await Assert.ThrowsAsync<SecretServiceException>(() => operation switch
        {
            "set" => store.SetAsync(Key, "unlabeled-secret"),
            "get" => store.GetAsync(Key),
            _ => store.DeleteAsync(Key)
        });

        Assert.Equal(7, exception.ExitCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("unlabeled-secret", exception.ToString());
        Assert.DoesNotContain("private-password", exception.ToString());
        Assert.DoesNotContain(Key.Service, exception.ToString());
        Assert.DoesNotContain(Key.Account, exception.ToString());
    }

    [Fact]
    public async Task EmptyExitOne_IsMissingValueAndSuccessfulClear()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var store = new SecretServiceSecretStore(CreateTool(temp, "exit 1"));

        Assert.Null(await store.GetAsync(Key));
        Assert.Equal(SecretStorageStatus.Unknown, store.GetStorageStatus(Key));
        await store.DeleteAsync(Key);
        await Assert.ThrowsAsync<SecretServiceException>(() => store.SetAsync(Key, "secret"));
    }

    [Theory]
    [InlineData("set")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task Cancellation_TerminatesStartedCommandAndPropagatesCancellation(string operation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        // The process test program lives in a temporary path that contains no shell quoting characters.
        var marker = temp.GetPath("pid");
        var tool = CreateTool(temp, $"printf '%s' \"$$\" > '{marker}'\nwhile :; do /bin/sleep 1; done");
        var store = new SecretServiceSecretStore(tool);
        using var cts = new CancellationTokenSource();
        var operationTask = operation switch
        {
            "set" => store.SetAsync(Key, new string('s', 256 * 1024), cts.Token),
            "get" => store.GetAsync(Key, cts.Token),
            _ => store.DeleteAsync(Key, cts.Token)
        };
        try
        {
            await WaitUntilAsync(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
            var pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operationTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(IsRunning(pid));
        }
        finally
        {
            cts.Cancel();
            try { await operationTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task Timeout_CoversBlockedInputWriteAndTerminatesCommand()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var marker = temp.GetPath("pid");
        var tool = CreateTool(temp, $"printf '%s' \"$$\" > '{marker}'\nwhile :; do /bin/sleep 1; done");
        var store = new SecretServiceSecretStore(tool, TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            store.SetAsync(Key, new string('s', 256 * 1024)).WaitAsync(TimeSpan.FromSeconds(5)));

        var pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task PreCanceledCommand_DoesNotStartProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var marker = temp.GetPath("started");
        var store = new SecretServiceSecretStore(CreateTool(temp, $"touch '{marker}'"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetAsync(Key, "secret", cts.Token));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task FailedCommand_CentralDiagnosticFileContainsOnlySafeMetadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var tool = CreateTool(temp, "printf 'private-service private-account unlabeled-secret' >&2\nexit 7");
        var logDirectory = temp.GetPath("Logs", "Exceptions");
        var store = new CompositeSecretStore(new SecretServiceSecretStore(tool), new InMemorySecretStore(),
            new ErrorService(logDirectory));

        Assert.Equal(SecretStorageStatus.SessionOnly, await store.SetWithStatusAsync(Key, "unlabeled-secret"));

        var log = await File.ReadAllTextAsync(Path.Combine(logDirectory, "errors.jsonl"));
        Assert.Contains("SecretStore.PrimaryWriteFailed", log);
        Assert.Contains("\"ExitCode\":\"7\"", log);
        Assert.DoesNotContain("private-service", log);
        Assert.DoesNotContain("private-account", log);
        Assert.DoesNotContain("unlabeled-secret", log);
        Assert.DoesNotContain(tool, log);
    }

    private static string CreateTool(TemporaryDirectory temp, string script)
    {
        var path = temp.CreateFile($"secret-tool-test-{Guid.NewGuid():N}.sh", "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The test secret-tool did not start in time.");
    }
}
