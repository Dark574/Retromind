using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogQueuedDownloadTests
{
    [Fact]
    public async Task Archive_MergesMainGameAndDlcWithoutOverwritingDifferentFiles()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var settings = new DownloadSettings { GogArchiveDirectory = "Offline" };
        var package = Package();
        var first = GogInstallerArchive.GetPackageDirectory(settings, package);
        var second = GogInstallerArchive.GetPackageDirectory(settings,
            package with { GameId = "456", InstallerName = "DLC", Version = "2" }, "123", "Game");
        Assert.Equal(first, second);
        Assert.StartsWith(temp.GetPath("Offline"), first);

        var staging = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        var file = Path.Combine(staging, "setup.sh");
        await File.WriteAllTextAsync(file, "installer");
        var downloaded = new GogDownloadedInstallerPackage(staging, file, [file]);
        await GogInstallerArchive.PublishAsync(first, downloaded, CancellationToken.None);
        await GogInstallerArchive.PublishAsync(first, downloaded, CancellationToken.None);
        var dlcFile = Path.Combine(staging, "dlc.sh");
        await File.WriteAllTextAsync(dlcFile, "dlc installer");
        await GogInstallerArchive.PublishAsync(second, new(staging, dlcFile, [dlcFile]), CancellationToken.None);
        Assert.Equal(["dlc.sh", "setup.sh"], Directory.GetFiles(first).Select(Path.GetFileName).Order());
        Assert.Equal("dlc installer", await File.ReadAllTextAsync(Path.Combine(first, "dlc.sh")));
        Assert.Empty(Directory.GetDirectories(first));
        Assert.Equal(first, GogInstallerArchive.GetPackageDirectory(settings, package, gameTitle: "Renamed Game"));
        GogInstallerArchive.DeleteQueuedStagingBestEffort(package);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("installer", await File.ReadAllTextAsync(Path.Combine(first, "setup.sh")));
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(file, "changed");
        var extra = Path.Combine(staging, "extra.bin");
        await File.WriteAllTextAsync(extra, "extra");
        await Assert.ThrowsAsync<IOException>(() => GogInstallerArchive.PublishAsync(first,
            new(staging, file, [extra, file]), CancellationToken.None));
        Assert.Equal("installer", await File.ReadAllTextAsync(Path.Combine(first, "setup.sh")));
        Assert.False(File.Exists(Path.Combine(first, "extra.bin")));
        Assert.True(File.Exists(file));
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(first)!),
            path => Path.GetFileName(path).StartsWith(".incoming-", StringComparison.Ordinal));
    }

    [Fact]
    public void Archive_SanitizesTitlesAndSeparatesDifferentGames()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var settings = new DownloadSettings { GogArchiveDirectory = "Offline" };
        var first = GogInstallerArchive.GetPackageDirectory(settings, Package(), gameTitle: "../Spiele: Ä & Test/");
        var second = GogInstallerArchive.GetPackageDirectory(settings, Package() with { GameId = "456" },
            gameTitle: "../Spiele: Ä & Test/");
        Assert.Equal(temp.GetPath("Offline"), Path.GetDirectoryName(first));
        Assert.Contains("Ä", Path.GetFileName(first));
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(':', Path.GetFileName(first));
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
    public async Task Archive_RejectsExistingSymlinkFileWithoutChangingItsTarget()
    {
        using var temp = new TemporaryDirectory();
        var archive = temp.CreateDirectory("archive");
        var source = temp.CreateDirectory("source");
        var target = temp.GetPath("valuable-file");
        await File.WriteAllTextAsync(target, "keep");
        File.CreateSymbolicLink(Path.Combine(archive, "setup.sh"), target);
        var file = Path.Combine(source, "setup.sh");
        await File.WriteAllTextAsync(file, "installer");

        await Assert.ThrowsAsync<IOException>(() => GogInstallerArchive.PublishAsync(archive,
            new(source, file, [file]), CancellationToken.None));

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(archive));
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
