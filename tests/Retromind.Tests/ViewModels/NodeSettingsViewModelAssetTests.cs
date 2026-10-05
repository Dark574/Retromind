using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class NodeSettingsViewModelAssetTests
{
    [Fact]
    public async Task ImportThenCancel_DoesNotCopyOrAssignNodeAsset()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var sourcePath = temp.CreateFile(Path.Combine("Incoming", "logo.png"), "logo");
        var node = new MediaNode("Games", NodeType.Area);
        var viewModel = CreateViewModel(node);

        viewModel.NodeLogoPath = await viewModel.ImportNodeAssetAsync(sourcePath, AssetType.Logo);
        viewModel.CancelCommand.Execute(null);

        Assert.Empty(node.Assets);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.LibraryRoot, "Games", "Logo")));
    }

    [Fact]
    public async Task ImportThenSave_CopiesAndAssignsNodeAsset()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var sourcePath = temp.CreateFile(Path.Combine("Incoming", "logo.png"), "logo");
        var node = new MediaNode("Games", NodeType.Area);
        var viewModel = CreateViewModel(node);

        viewModel.NodeLogoPath = await viewModel.ImportNodeAssetAsync(sourcePath, AssetType.Logo);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var asset = Assert.Single(node.Assets);
        Assert.Equal(AssetType.Logo, asset.Type);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.NotEqual(sourcePath, asset.AbsolutePath);
    }

    private static NodeSettingsViewModel CreateViewModel(MediaNode node)
    {
        var roots = new ObservableCollection<MediaNode> { node };
        return new NodeSettingsViewModel(
            node,
            roots,
            new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot),
            [node.Name]);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
