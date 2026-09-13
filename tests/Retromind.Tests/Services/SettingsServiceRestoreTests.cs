using System.Reflection;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class SettingsServiceRestoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedRestore_RejectsRegularSavesIncludingDelayedSnapshots(
        bool saveModel,
        bool restoreSettings)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var oldSettings = new AppSettings { ItemWidth = 111 };
        var oldJson = service.Serialize(oldSettings);
        await service.SaveJsonAsync(oldJson);
        var releaseOldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task SaveOldSnapshotAsync() => saveModel
            ? service.SaveAsync(oldSettings)
            : service.SaveJsonAsync(oldJson);

        async Task DelayedSaveAsync()
        {
            // Model a save whose snapshot predates restore, but whose continuation
            // has not yet entered the settings service. It has no cancellation token.
            await releaseOldSave.Task;
            await SaveOldSnapshotAsync();
        }

        var delayedSave = DelayedSaveAsync();
        Assert.False(delayedSave.IsCompleted);
        using (var restore = await service.BeginRestoreAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(SaveOldSnapshotAsync);
            if (restoreSettings)
                await service.RestoreJsonAsync(service.Serialize(new AppSettings { ItemWidth = 234 }));
            restore.Complete();
        }

        releaseOldSave.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayedSave);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(SaveOldSnapshotAsync);

        Assert.Equal(restoreSettings ? 234 : 111, ReadItemWidth(temp));
        // Restart creates a new service instance and enables regular saving again.
        await new SettingsService().SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    [Fact]
    public async Task FailedRestore_UnblocksRegularSavesAfterValidationFailure()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        await service.SaveAsync(new AppSettings { ItemWidth = 111 });

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            using var restore = await service.BeginRestoreAsync();
            await service.RestoreJsonAsync("{ invalid JSON");
            restore.Complete();
        });

        Assert.Equal(111, ReadItemWidth(temp));
        await service.SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    [Fact]
    public async Task FailedRestore_UnblocksRegularSavesAfterWriteFailure()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        await service.SaveAsync(new AppSettings { ItemWidth = 111 });
        var obstruction = temp.CreateDirectory("app_settings.json.tmp");

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var restore = await service.BeginRestoreAsync();
            await service.RestoreJsonAsync(service.Serialize(new AppSettings { ItemWidth = 234 }));
            restore.Complete();
        });

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(111, ReadItemWidth(temp));
        Directory.Delete(obstruction); // Only the empty directory created above, inside the test root.
        await service.SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    [Fact]
    public async Task IncompleteRestore_AllowsRollbackBeforeUnblockingRegularSaves()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var originalJson = service.Serialize(new AppSettings { ItemWidth = 111 });
        await service.SaveJsonAsync(originalJson);

        using (await service.BeginRestoreAsync())
        {
            await service.RestoreJsonAsync(service.Serialize(new AppSettings { ItemWidth = 234 }));
            Assert.Equal(234, ReadItemWidth(temp));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveJsonAsync(originalJson));

            // A coordinator can undo the settings replacement if the larger operation fails.
            await service.RestoreJsonAsync(originalJson);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new AppSettings()));
        }

        Assert.Equal(111, ReadItemWidth(temp));
        await service.SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledSave_DoesNotWriteOrCleanUpAnActiveTransaction(bool cancelWhileWaiting)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        await service.SaveAsync(new AppSettings { ItemWidth = 111 });
        var tempPath = temp.CreateFile("app_settings.json.tmp", "active transaction");
        using var cancellation = new CancellationTokenSource();
        var gate = GetIoGate(service);

        await gate.WaitAsync();
        try
        {
            if (!cancelWhileWaiting)
                cancellation.Cancel();

            var save = service.SaveJsonAsync("{}", cancellation.Token);
            if (cancelWhileWaiting)
            {
                Assert.False(save.IsCompleted);
                cancellation.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => save.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, gate.CurrentCount);
            Assert.Equal("active transaction", File.ReadAllText(tempPath));
            Assert.Equal(111, ReadItemWidth(temp));
        }
        finally
        {
            gate.Release();
        }

        await service.SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    [Fact]
    public async Task QueuedSave_RechecksRestoreBlockAfterAcquiringIoGate()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var oldJson = service.Serialize(new AppSettings { ItemWidth = 111 });
        await service.SaveJsonAsync(oldJson);
        var gate = GetIoGate(service);

        await gate.WaitAsync();
        Task<SettingsService.RestoreScope> pendingRestore;
        Task queuedSave;
        try
        {
            // Both calls are waiting at the IO boundary. The regular save has
            // passed its initial guard before the earlier restore request can block it.
            pendingRestore = service.BeginRestoreAsync();
            queuedSave = service.SaveJsonAsync(oldJson);
            Assert.False(pendingRestore.IsCompleted);
            Assert.False(queuedSave.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        using (var restore = await pendingRestore.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await service.RestoreJsonAsync(service.Serialize(new AppSettings { ItemWidth = 234 }));
            restore.Complete();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queuedSave.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(234, ReadItemWidth(temp));
    }

    [Fact]
    public async Task BeginRestoreAsync_WaitsForActiveIoAndRejectsOverlappingScopes()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var gate = GetIoGate(service);

        await gate.WaitAsync();
        Task<SettingsService.RestoreScope> pendingRestore;
        try
        {
            pendingRestore = service.BeginRestoreAsync();
            Assert.False(pendingRestore.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        using (await pendingRestore.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginRestoreAsync());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveJsonAsync("{}"));
        }

        await service.SaveAsync(new AppSettings { ItemWidth = 345 });
        Assert.Equal(345, ReadItemWidth(temp));
    }

    // Hold the existing IO boundary to control scheduling without sleeps or production test hooks.
    private static SemaphoreSlim GetIoGate(SettingsService service)
        => Assert.IsType<SemaphoreSlim>(typeof(SettingsService)
            .GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));

    private static double ReadItemWidth(TemporaryDirectory temp)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(temp.GetPath("app_settings.json")));
        return json.RootElement.GetProperty(nameof(AppSettings.ItemWidth)).GetDouble();
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")), ("APPDIR", null));
}
