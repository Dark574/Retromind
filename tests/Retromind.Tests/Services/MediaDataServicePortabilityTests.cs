using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class MediaDataServicePortabilityTests
{
    [Fact]
    public async Task SavedPortableLibrary_ResolvesAgainstNewRootAfterMove()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        var relativeLaunchPath = Path.Combine("Library", "Games", "Portable Game", "game.exe");
        var relativeWorkingDirectory = Path.Combine("Library", "Games", "Portable Game");
        var relativeAssetPath = Path.Combine("Library", "Games", "Portable Game", "Cover", "cover.png");
        var relativePrefixPath = Path.Combine("Prefixes", "Portable_Game");

        using (UseDataRoot(firstRoot.RootPath))
        {
            var item = new MediaItem
            {
                Title = "Portable Game",
                Files =
                [
                    new MediaFileRef
                    {
                        Kind = MediaFileKind.LibraryRelative,
                        Path = relativeLaunchPath
                    }
                ],
                WorkingDirectory = relativeWorkingDirectory,
                PrefixPath = relativePrefixPath,
                Assets = new ObservableCollection<MediaAsset>
                {
                    new()
                    {
                        Type = AssetType.Cover,
                        RelativePath = relativeAssetPath
                    }
                }
            };
            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] =
                Path.Combine("Library", "Games", "Portable Game");
            var node = new MediaNode
            {
                Name = "Portable Node",
                ThemePath = Path.Combine("Themes", "Default", "theme.axaml")
            };
            node.Items.Add(item);

            var service = new MediaDataService();
            var json = service.Serialize(new ObservableCollection<MediaNode> { node });
            await service.SaveJsonAsync(json);
        }

        File.Copy(
            firstRoot.GetPath("retromind_tree.json"),
            secondRoot.GetPath("retromind_tree.json"));

        using (UseDataRoot(secondRoot.RootPath))
        {
            var loadedRoots = await new MediaDataService().LoadAsync();
            var loadedNode = Assert.Single(loadedRoots);
            var loadedItem = Assert.Single(loadedNode.Items);
            var loadedAsset = Assert.Single(loadedItem.Assets);

            Assert.Equal(secondRoot.GetPath(relativeLaunchPath), loadedItem.GetPrimaryLaunchPath());
            Assert.Equal(
                secondRoot.GetPath(relativeWorkingDirectory),
                AppPaths.ResolveDataPath(loadedItem.WorkingDirectory!));
            Assert.Equal(
                secondRoot.GetPath("Library", relativePrefixPath),
                Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, loadedItem.PrefixPath!)));
            Assert.Equal(secondRoot.GetPath(relativeAssetPath), loadedAsset.AbsolutePath);
            Assert.True(GogInstallPathHelper.TryResolveStoredPath(
                loadedItem.CustomFields[CustomFieldKeyHelper.StoreInstallPath],
                out var resolvedInstallPath));
            Assert.Equal(secondRoot.GetPath("Library", "Games", "Portable Game"), resolvedInstallPath);
            Assert.Equal(
                secondRoot.GetPath("Themes", "Default", "theme.axaml"),
                AppPaths.ResolveDataPathInsideRootOrEmpty(loadedNode.ThemePath));
        }
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
