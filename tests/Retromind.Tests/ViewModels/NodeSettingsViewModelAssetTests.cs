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
        var node = new MediaNode("Games");
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
        var node = new MediaNode("Games");
        var viewModel = CreateViewModel(node);

        viewModel.NodeLogoPath = await viewModel.ImportNodeAssetAsync(sourcePath, AssetType.Logo);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var asset = Assert.Single(node.Assets);
        Assert.Equal(AssetType.Logo, asset.Type);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.NotEqual(sourcePath, asset.AbsolutePath);
    }

    [Fact]
    public async Task Save_PortableModeNormalizesKnownNodeEnvironmentPathOnly()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var protonPath = temp.GetPath("Emulators", "ProtonVersions", "GE-Proton");
        var customValue = temp.GetPath("custom", "unchanged");
        var node = new MediaNode("Games")
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["PROTONPATH"] = protonPath,
                ["RETROMIND_CUSTOM"] = customValue
            }
        };
        var settings = new AppSettings { PreferPortableLaunchPaths = true };
        var viewModel = CreateViewModel(node, settings);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            Path.Combine("Emulators", "ProtonVersions", "GE-Proton"),
            node.EnvironmentOverrides!["PROTONPATH"]);
        Assert.Equal(customValue, node.EnvironmentOverrides["RETROMIND_CUSTOM"]);
    }

    private static NodeSettingsViewModel CreateViewModel(MediaNode node, AppSettings? settings = null)
    {
        var roots = new ObservableCollection<MediaNode> { node };
        return new NodeSettingsViewModel(
            node,
            roots,
            settings ?? new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService()),
            [node.Name]);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
