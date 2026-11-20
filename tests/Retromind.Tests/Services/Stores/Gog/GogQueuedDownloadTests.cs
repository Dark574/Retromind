using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogQueuedDownloadTests
{
    [Fact]
    public async Task Archive_PublishesVersionedCopyAndNeverOverwritesIt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var settings = new DownloadSettings { GogArchiveDirectory = "Offline" };
        var package = Package();
        var first = GogInstallerArchive.GetPackageDirectory(settings, package);
        var second = GogInstallerArchive.GetPackageDirectory(settings, package with { Version = "2" });
        Assert.NotEqual(first, second);
        Assert.StartsWith(temp.GetPath("Offline"), first);

        var staging = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        var file = Path.Combine(staging, "setup.sh");
        await File.WriteAllTextAsync(file, "installer");
        var downloaded = new GogDownloadedInstallerPackage(staging, file, [file]);
        await GogInstallerArchive.PublishAsync(first, downloaded, CancellationToken.None);
        GogInstallerArchive.DeleteQueuedStagingBestEffort(package);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("installer", await File.ReadAllTextAsync(Path.Combine(first, "setup.sh")));
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(file, "changed");
        await Assert.ThrowsAsync<IOException>(() => GogInstallerArchive.PublishAsync(first, downloaded, CancellationToken.None));
        Assert.Equal("installer", await File.ReadAllTextAsync(Path.Combine(first, "setup.sh")));
    }

    [Fact]
    public void Archive_RejectsSymlinkAndConflictingInstallerNames()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var destination = Directory.CreateDirectory(temp.GetPath("real")).FullName;
        var link = temp.GetPath("link");
        Directory.CreateSymbolicLink(link, destination);

        Assert.Throws<IOException>(() => GogInstallerArchive.GetPackageDirectory(
            new DownloadSettings { GogArchiveDirectory = link }, Package()));
        Assert.Throws<IOException>(() => GogInstallerArchive.GetSafeFileNames(Package() with
        {
            Files = [new("https://example.test", "file", 2), new("https://example.test", "file.part", 3)]
        }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void Space_AccountsForStagedBytesAndSharedOrSeparateFilesystems()
    {
        using var temp = new TemporaryDirectory();
        var package = Package();
        var staging = Directory.CreateDirectory(temp.GetPath("staging")).FullName;
        File.WriteAllBytes(Path.Combine(staging, "setup.sh.part"), new byte[40]);
        var reserve = GogInstallSpaceService.FreeSpaceReserveBytes;
        var assessment = GogInstallSpaceService.Assess(package, staging, staging, reserve + 1000);

        Assert.Equal(60, assessment.RemainingDownloadBytes);
        Assert.True(GogInstallSpaceService.CanFitAdditionalCopy(assessment, reserve + 100, sharedFileSystem: false));
        Assert.False(GogInstallSpaceService.CanFitAdditionalCopy(assessment, reserve + 99, sharedFileSystem: false));
        Assert.False(GogInstallSpaceService.CanFitAdditionalCopy(assessment, reserve + 100, sharedFileSystem: true));
        Assert.True(GogInstallSpaceService.CanFitAdditionalCopy(assessment, reserve + 160, sharedFileSystem: true));
    }

    [Fact]
    public async Task RateLimit_UsesElapsedTimeAndCancelsPendingDelay()
    {
        var time = new RecordingTime();
        var limiter = new DownloadRateLimiter(100, time);
        using var stop = new CancellationTokenSource();

        var first = limiter.WaitAsync(50, stop.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(500), time.Delay);
        time.Advance();
        await first;
        time.Idle(TimeSpan.FromSeconds(10));
        await limiter.WaitAsync(50, stop.Token);
        var pending = limiter.WaitAsync(25, stop.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(250), time.Delay);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static GogInstallerPackage Package() => new("123", GogInstallPlatform.Linux, "Game", "1",
        [new GogInstallerDownloadFile("https://example.test/installer", "setup.sh", 100)]);

    private sealed class RecordingTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private long _ticks;
        public TimeSpan Delay { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            Delay = dueTime;
            return new TestTimer();
        }
        public void Advance()
        {
            Idle(Delay);
            _callback!(_state);
        }
        public void Idle(TimeSpan elapsed) => _ticks += elapsed.Ticks;

        private sealed class TestTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
