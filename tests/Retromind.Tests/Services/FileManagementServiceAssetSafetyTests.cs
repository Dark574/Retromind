using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class FileManagementServiceAssetSafetyTests
{
    [Theory]
    [InlineData(AssetType.Cover, "cover.png", "My_Game__12345678_Cover_01.png")]
    [InlineData(AssetType.Manual, "Quick Guide.pdf", "Quick_Guide__RM__My_Game__12345678_Manual_01.pdf")]
    [InlineData(AssetType.Music, "Main Theme.ogg", "Main_Theme__RM__My_Game__12345678_Music_01.ogg")]
    public async Task Import_PublishesCompleteFileWithExistingNameAndRelativePath(
        AssetType type, string sourceName, string expectedName)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile($"Incoming/{sourceName}", "complete asset");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);
        var changes = 0;
        service.LibraryChanged += () => changes++;

        var asset = await service.ImportAssetAsync(source, CreateItem(), ["Games"], type);

        Assert.NotNull(asset);
        Assert.Equal(Path.Combine("Library", "Games", type.ToString(), expectedName), asset.RelativePath);
        Assert.False(Path.IsPathRooted(asset.RelativePath));
        Assert.Equal("complete asset", await File.ReadAllTextAsync(asset.AbsolutePath));
        Assert.Equal("complete asset", await File.ReadAllTextAsync(source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(asset.AbsolutePath)!));
        Assert.Equal(1, changes);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Import_ExistingAssetGetsNextCounterWithoutReplacingOldFile()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var original = temp.CreateFile("Library/Games/Cover/My_Game__12345678_Cover_01.png", "original");
        var source = temp.CreateFile("Incoming/new.png", "new");
        var service = new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService());

        var asset = await service.ImportAssetAsync(source, CreateItem(), ["Games"], AssetType.Cover);

        Assert.NotNull(asset);
        Assert.Equal("My_Game__12345678_Cover_02.png", Path.GetFileName(asset.RelativePath));
        Assert.Equal("original", await File.ReadAllTextAsync(original));
        Assert.Equal("new", await File.ReadAllTextAsync(asset.AbsolutePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyFailureAfterPartialWrite_RemovesTemporaryFileAndReportsOnce(bool transactional)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile("Incoming/new.png");
        var destinationFolder = temp.GetPath("Library", "Games", "Cover");
        var sawPartialFile = false;
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new FileManagementService(AppPaths.LibraryRoot, errors, _ => new InterruptedSource(() =>
        {
            var partialFile = Assert.Single(Directory.GetFiles(destinationFolder));
            Assert.StartsWith(".retromind-asset-", Path.GetFileName(partialFile));
            Assert.EndsWith(".tmp", partialFile);
            Assert.True(new FileInfo(partialFile).Length > 0);
            sawPartialFile = true;
        }));
        var changes = 0;
        service.LibraryChanged += () => changes++;

        if (transactional)
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
                [new(source, AssetType.Cover)], []));
        else
            Assert.Null(await service.ImportAssetAsync(source, CreateItem(), ["Games"], AssetType.Cover));

        Assert.True(sawPartialFile);
        Assert.Empty(Directory.GetFiles(destinationFolder));
        Assert.True(File.Exists(source));
        Assert.Equal(0, changes);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("FileManagement.AssetImportFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal(nameof(FileManagementService), report.Component);
        Assert.Equal("Cover", report.Context!["AssetType"]);
        Assert.Equal(source, report.Context["SourcePath"]);
        Assert.EndsWith("My_Game__12345678_Cover_01.png", report.Context["TargetPath"]);
        Assert.All(errors.Notifications, notification => Assert.Null(notification));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationCreatedDuringCopy_IsNeverOverwritten(bool transactional)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile("Incoming/new.png", "new");
        var target = temp.GetPath("Library", "Games", "Cover", "My_Game__12345678_Cover_01.png");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors, path =>
        {
            File.WriteAllText(target, "concurrent asset");
            return File.OpenRead(path);
        });

        if (transactional)
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
                [new(source, AssetType.Cover)], []));
        else
            Assert.Null(await service.ImportAssetAsync(source, CreateItem(), ["Games"], AssetType.Cover));

        Assert.Equal("concurrent asset", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
        Assert.Equal("FileManagement.AssetImportFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task SecondTransactionalCopyFails_RemovesBothCompletedImportAndPartialFile()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var first = temp.CreateFile("Incoming/first.png", "first");
        var second = temp.CreateFile("Incoming/second.png");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors,
            path => path == second ? new InterruptedSource() : File.OpenRead(path));

        await Assert.ThrowsAsync<IOException>(() => service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
            [new(first, AssetType.Cover), new(second, AssetType.Cover)], []));

        Assert.Empty(Directory.GetFiles(temp.GetPath("Library", "Games", "Cover")));
        Assert.Equal("FileManagement.AssetImportFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task FailedRestore_PreservesQuarantineBlocksCommitAndAllowsExplicitRetry()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var original = temp.CreateFile("Library/Games/Cover/Old__12345678_Cover_01.png", "original");
        var source = temp.CreateFile("Incoming/new.png", "new");
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);
        using var transaction = await service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
            [new(source, AssetType.Cover)], [AssetFor(original)]);
        var quarantineFile = Assert.Single(Directory.GetFiles(transaction.QuarantineDirectory));
        var imported = Assert.Single(transaction.ImportedAssets).AbsolutePath;
        await File.WriteAllTextAsync(original, "conflicting file");

        await Assert.ThrowsAsync<AggregateException>(() => Task.Run(transaction.Rollback));
        Assert.Throws<InvalidOperationException>(transaction.Commit);
        transaction.Dispose();

        Assert.Equal("original", await File.ReadAllTextAsync(quarantineFile));
        Assert.Equal("conflicting file", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(imported));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("FileManagement.AssetRollbackFailed", report.Code);
        Assert.Equal("RestoreQuarantinedAsset", report.Context!["Operation"]);
        Assert.Equal("Cover", report.Context["AssetType"]);
        Assert.Equal(quarantineFile, report.Context["SourcePath"]);
        Assert.Equal(original, report.Context["TargetPath"]);

        File.Delete(original);
        // A successful step must not run again and delete an unrelated file created afterwards.
        await File.WriteAllTextAsync(imported, "later unrelated file");
        transaction.Rollback();

        Assert.Equal("original", await File.ReadAllTextAsync(original));
        Assert.Equal("later unrelated file", await File.ReadAllTextAsync(imported));
        Assert.False(Directory.Exists(transaction.QuarantineDirectory));
        Assert.Single(errors.Reports);
    }

    [Fact]
    public async Task DisposeWithFailedRestore_PreservesOriginalWithoutRepeatingDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var original = temp.CreateFile("Library/Games/Cover/old.png", "original");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);
        var transaction = await service.PrepareAssetTransactionAsync(CreateItem(), ["Games"], [], [AssetFor(original)]);
        File.WriteAllText(original, "conflict");

        transaction.Dispose();
        transaction.Dispose();

        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(transaction.QuarantineDirectory))));
        Assert.Single(errors.Reports);
        File.Delete(original);
        transaction.Rollback();
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public async Task CommitCleanupFailure_DoesNotRollbackCommittedAssetsOrThrow()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var original = temp.CreateFile("Library/Games/Cover/old.png", "original");
        var source = temp.CreateFile("Incoming/new.png", "new");
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new FileManagementService(AppPaths.LibraryRoot, errors, openAssetSource: null,
            deleteQuarantineDirectory: _ => throw new IOException("cleanup unavailable"));
        var changes = 0;
        service.LibraryChanged += () => changes++;
        using var transaction = await service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
            [new(source, AssetType.Cover)], [AssetFor(original)]);

        transaction.Commit();
        transaction.Dispose();
        transaction.Commit();

        Assert.False(File.Exists(original));
        Assert.Equal("new", File.ReadAllText(Assert.Single(transaction.ImportedAssets).AbsolutePath));
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(transaction.QuarantineDirectory))));
        Assert.Equal(1, changes);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("FileManagement.AssetCleanupFailed", report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
        Assert.Equal("Cover", report.Context!["AssetType"]);
        Assert.Equal(transaction.QuarantineDirectory, report.Context["SourcePath"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeDestination_RefusesImportWithoutExternalChanges(bool transactional)
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var node = temp.CreateDirectory("Library", "Games");
        Directory.CreateSymbolicLink(Path.Combine(node, "Cover"), external.RootPath);
        var source = temp.CreateFile("Incoming/new.png", "new");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);

        if (transactional)
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAssetTransactionAsync(CreateItem(), ["Games"],
                [new(source, AssetType.Cover)], []));
        else
            Assert.Null(await service.ImportAssetAsync(source, CreateItem(), ["Games"], AssetType.Cover));

        Assert.Empty(Directory.GetFileSystemEntries(external.RootPath));
        Assert.Equal("FileManagement.AssetImportFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task SymlinkCreatedAtDestinationDuringCopy_IsPreservedWithoutWritingToItsTarget()
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile("Incoming/new.png", "new");
        var externalFile = external.CreateFile("outside.png", "outside");
        var target = temp.GetPath("Library", "Games", "Cover", "My_Game__12345678_Cover_01.png");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors, path =>
        {
            File.CreateSymbolicLink(target, externalFile);
            return File.OpenRead(path);
        });

        Assert.Null(await service.ImportAssetAsync(source, CreateItem(), ["Games"], AssetType.Cover));

        Assert.NotNull(new FileInfo(target).LinkTarget);
        Assert.Equal("outside", File.ReadAllText(externalFile));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!));
        Assert.Single(errors.Reports);
    }

    [Fact]
    public async Task UnsafeQuarantineDuringRollback_PreservesRecoveryFilesAndAllowsRetry()
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var original = temp.CreateFile("Library/Games/Cover/old.png", "original");
        var externalFile = external.CreateFile("outside.png", "outside");
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);
        using var transaction = await service.PrepareAssetTransactionAsync(CreateItem(), ["Games"], [], [AssetFor(original)]);
        var savedQuarantine = transaction.QuarantineDirectory + "-saved";
        Directory.Move(transaction.QuarantineDirectory, savedQuarantine);
        Directory.CreateSymbolicLink(transaction.QuarantineDirectory, external.RootPath);

        Assert.Throws<AggregateException>(transaction.Rollback);
        Assert.Throws<InvalidOperationException>(transaction.Commit);
        transaction.Dispose();

        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(savedQuarantine))));
        Assert.Equal("outside", File.ReadAllText(externalFile));
        Assert.Single(errors.Reports);
        Directory.Delete(transaction.QuarantineDirectory);
        Directory.Move(savedQuarantine, transaction.QuarantineDirectory);
        transaction.Rollback();
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public void RenameFailure_IsReportedOnceAndPreservesSourceAndRelativeReference()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile("Library/Games/Cover/My_Game__12345678_Cover_01.png", "original");
        var target = temp.CreateDirectory("Library", "Games", "Cover", "Renamed__12345678_Cover_01.png");
        var item = CreateItem();
        item.Assets.Add(AssetFor(source));
        var originalReference = item.Assets[0].RelativePath;
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);

        Assert.False(service.RenameItemAssets(item, item.Title, "Renamed", ["Games"]));

        Assert.Equal("original", File.ReadAllText(source));
        Assert.Equal(originalReference, item.Assets[0].RelativePath);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("FileManagement.AssetRenameFailed", report.Code);
        Assert.Equal(source, report.Context!["SourcePath"]);
        Assert.Equal(target, report.Context["TargetPath"]);
    }

    [Fact]
    public void Rename_KeepsDisplayPrefixCounterAndPortablePath()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var source = temp.CreateFile("Library/Games/Manual/Quick_Guide__RM__My_Game__12345678_Manual_03.pdf", "manual");
        var item = CreateItem();
        var asset = AssetFor(source, AssetType.Manual);
        item.Assets.Add(asset);
        var errors = new RecordingErrorService();
        var service = new FileManagementService(AppPaths.LibraryRoot, errors);

        Assert.True(service.RenameItemAssets(item, item.Title, "Renamed", ["Games"]),
            string.Join(Environment.NewLine, errors.Reports.Select(report => report.Exception?.ToString())));

        Assert.Equal(Path.Combine("Library", "Games", "Manual", "Quick_Guide__RM__Renamed__12345678_Manual_03.pdf"), asset.RelativePath);
        Assert.Equal("manual", File.ReadAllText(asset.AbsolutePath));
        Assert.False(File.Exists(source));
        Assert.Empty(errors.Reports);
    }

    private static MediaItem CreateItem() => new("My Game") { Id = "12345678-rest" };

    private static MediaAsset AssetFor(string path, AssetType type = AssetType.Cover) =>
        new() { Type = type, RelativePath = Path.GetRelativePath(AppPaths.DataRoot, path) };

    private static EnvironmentVariableScope UseDataRoot(TemporaryDirectory temp) =>
        new(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));

    private sealed class InterruptedSource(Action? beforeFailure = null) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                beforeFailure?.Invoke();
                return ValueTask.FromException<int>(new IOException("Source interrupted after partial data."));
            }
            _read = true;
            buffer.Span.Fill(42);
            return ValueTask.FromResult(buffer.Length);
        }
    }
}
