using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class FileManagementServiceAssetTransactionTests
{
    [Fact]
    public async Task DisposeWithoutCommit_RestoresDeletionAndRemovesImport()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new FileManagementService(AppPaths.LibraryRoot);
        var originalPath = temp.CreateFile(
            Path.Combine("Library", "Games", "Cover", "Old__12345678_Cover_01.jpg"),
            "old");
        var sourcePath = temp.CreateFile(Path.Combine("Incoming", "new.png"), "new");
        var item = CreateItem(originalPath);

        string importedPath;
        using (var transaction = await service.PrepareAssetTransactionAsync(
                   item,
                   ["Games"],
                   [new FileManagementService.AssetImportRequest(sourcePath, AssetType.Cover)],
                   [item.Assets[0]]))
        {
            importedPath = transaction.ImportedAssets[0].AbsolutePath;
            Assert.False(File.Exists(originalPath));
            Assert.True(File.Exists(importedPath));
        }

        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(importedPath));
    }

    [Fact]
    public async Task Commit_KeepsImportAndPermanentlyRemovesDeletion()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new FileManagementService(AppPaths.LibraryRoot);
        var originalPath = temp.CreateFile(
            Path.Combine("Library", "Games", "Cover", "Old__12345678_Cover_01.jpg"),
            "old");
        var sourcePath = temp.CreateFile(Path.Combine("Incoming", "new.png"), "new");
        var item = CreateItem(originalPath);

        using var transaction = await service.PrepareAssetTransactionAsync(
            item,
            ["Games"],
            [new FileManagementService.AssetImportRequest(sourcePath, AssetType.Cover)],
            [item.Assets[0]]);
        var importedPath = transaction.ImportedAssets[0].AbsolutePath;

        transaction.Commit();

        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(importedPath));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.DataRoot, ".asset-transactions")));
    }

    [Fact]
    public async Task PreparationFailure_RemovesAlreadyCopiedImports()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new FileManagementService(AppPaths.LibraryRoot);
        var sourcePath = temp.CreateFile(Path.Combine("Incoming", "new.png"), "new");
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        var unsafeAsset = new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.Combine("..", "outside.png")
        };

        await Assert.ThrowsAsync<IOException>(() => service.PrepareAssetTransactionAsync(
            item,
            ["Games"],
            [new FileManagementService.AssetImportRequest(sourcePath, AssetType.Cover)],
            [unsafeAsset]));

        var coverFolder = Path.Combine(AppPaths.LibraryRoot, "Games", AssetType.Cover.ToString());
        Assert.Empty(Directory.EnumerateFiles(coverFolder));
    }

    private static MediaItem CreateItem(string absolutePath)
    {
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.GetRelativePath(AppPaths.DataRoot, absolutePath)
        });
        return item;
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
