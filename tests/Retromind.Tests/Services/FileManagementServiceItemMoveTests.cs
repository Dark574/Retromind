using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class FileManagementServiceItemMoveTests
{
    [Fact]
    public void MoveItemAssets_MovesUniqueAssetAndUpdatesPortablePath()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);

        var sourceFile = CreateAsset(temp, "Source", "Game__12345678_Cover_01.jpg");
        var item = CreateItemWithCover(sourceFile);
        var service = new FileManagementService(AppPaths.LibraryRoot);

        var result = service.MoveItemAssets(item, ["Source"], ["Target"], [item]);

        var expectedTarget = Path.Combine(
            AppPaths.LibraryRoot,
            "Target",
            AssetType.Cover.ToString(),
            Path.GetFileName(sourceFile));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.MovedFileCount);
        Assert.Equal(0, result.CopiedFileCount);
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(expectedTarget));
        Assert.Equal(Path.GetRelativePath(AppPaths.DataRoot, expectedTarget), item.Assets[0].RelativePath);
    }

    [Fact]
    public void MoveItemAssets_CopiesAssetThatIsReferencedByAnotherItem()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);

        var sourceFile = CreateAsset(temp, "Source", "Shared__12345678_Cover_01.jpg");
        var movedItem = CreateItemWithCover(sourceFile);
        var otherItem = CreateItemWithCover(sourceFile);
        var originalRelativePath = otherItem.Assets[0].RelativePath;
        var service = new FileManagementService(AppPaths.LibraryRoot);

        var result = service.MoveItemAssets(
            movedItem,
            ["Source"],
            ["Target"],
            [movedItem, otherItem]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.MovedFileCount);
        Assert.Equal(1, result.CopiedFileCount);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal(originalRelativePath, otherItem.Assets[0].RelativePath);
        Assert.NotEqual(originalRelativePath, movedItem.Assets[0].RelativePath);
        Assert.True(File.Exists(movedItem.Assets[0].AbsolutePath));
    }

    [Fact]
    public void MoveItemAssets_WhenCommitFails_RestoresSourceAndLeavesModelUnchanged()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);

        var sourceFile = CreateAsset(temp, "Source", "Rollback__12345678_Cover_01.jpg");
        var item = CreateItemWithCover(sourceFile);
        var originalRelativePath = item.Assets[0].RelativePath;
        Directory.CreateDirectory(AppPaths.LibraryRoot);
        File.WriteAllText(Path.Combine(AppPaths.LibraryRoot, "BlockedTarget"), "not a directory");
        var service = new FileManagementService(AppPaths.LibraryRoot);

        var result = service.MoveItemAssets(item, ["Source"], ["BlockedTarget"], [item]);

        Assert.False(result.Success);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal(originalRelativePath, item.Assets[0].RelativePath);
    }

    private static string CreateAsset(TemporaryDirectory temp, string nodeName, string fileName)
    {
        return temp.CreateFile(
            Path.Combine("Library", nodeName, AssetType.Cover.ToString(), fileName),
            "image");
    }

    private static MediaItem CreateItemWithCover(string absolutePath)
    {
        var item = new MediaItem("Game");
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
