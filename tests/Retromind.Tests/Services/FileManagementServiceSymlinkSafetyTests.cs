using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class FileManagementServiceSymlinkSafetyTests
{
    [Fact]
    public void DeleteAssetFile_AncestorSymlink_PreservesExternalFile()
    {
        using var dataRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(dataRoot.RootPath);
        var libraryRoot = dataRoot.CreateDirectory("Library");
        var externalFile = externalRoot.CreateFile("outside.png", "must remain");
        var linkPath = Path.Combine(libraryRoot, "linked-node");
        Directory.CreateSymbolicLink(linkPath, externalRoot.RootPath);
        var asset = new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.GetRelativePath(
                AppPaths.DataRoot,
                Path.Combine(linkPath, Path.GetFileName(externalFile)))
        };
        var service = new FileManagementService(AppPaths.LibraryRoot);

        service.DeleteAssetFile(asset);

        Assert.Equal("must remain", File.ReadAllText(externalFile));
    }

    [Fact]
    public async Task ImportAssetAsync_LinkedAssetFolder_DoesNotWriteOutsideDataRoot()
    {
        using var dataRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(dataRoot.RootPath);
        var nodeFolder = dataRoot.CreateDirectory("Library", "Games");
        Directory.CreateSymbolicLink(
            Path.Combine(nodeFolder, AssetType.Manual.ToString()),
            externalRoot.RootPath);
        var sourcePath = dataRoot.CreateFile("Incoming/manual.pdf", "manual");
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        var service = new FileManagementService(AppPaths.LibraryRoot);

        var imported = await service.ImportAssetAsync(
            sourcePath,
            item,
            ["Games"],
            AssetType.Manual);

        Assert.Null(imported);
        Assert.Empty(Directory.EnumerateFileSystemEntries(externalRoot.RootPath));
    }

    [Fact]
    public void MoveItemAssets_LinkedTargetNode_RefusesMoveAndPreservesExternalDirectory()
    {
        using var dataRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(dataRoot.RootPath);
        var sourcePath = dataRoot.CreateFile(
            "Library/Source/Cover/Game__12345678_Cover_01.jpg",
            "image");
        var libraryRoot = Path.Combine(dataRoot.RootPath, "Library");
        Directory.CreateSymbolicLink(Path.Combine(libraryRoot, "Target"), externalRoot.RootPath);
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.GetRelativePath(AppPaths.DataRoot, sourcePath)
        });
        var service = new FileManagementService(AppPaths.LibraryRoot);

        var result = service.MoveItemAssets(item, ["Source"], ["Target"], [item]);

        Assert.False(result.Success);
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(externalRoot.RootPath));
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
