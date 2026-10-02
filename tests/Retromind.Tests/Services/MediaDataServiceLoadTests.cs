using System.Collections.ObjectModel;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class MediaDataServiceLoadTests
{
    [Fact]
    public void Serialize_ItemWithoutGogDlcState_OmitsDlcProperty()
    {
        var roots = new ObservableCollection<MediaNode>
        {
            new()
            {
                Name = "Node",
                Items = new ObservableCollection<MediaItem> { new("Game") }
            }
        };

        var json = new MediaDataService().Serialize(roots);

        Assert.DoesNotContain("GogDlcInstallations", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_OmitsComputedAssetPropertiesButKeepsRelativePaths()
    {
        var node = new MediaNode { Name = "Node" };
        node.Assets.Add(new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = "Library/Node/Cover/node.png"
        });

        var item = new MediaItem("Game");
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Logo,
            RelativePath = "Library/Games/Game/Logo/logo.png"
        });
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Manual,
            RelativePath = "Library/Games/Game/Manual/manual.pdf"
        });
        node.Items.Add(item);

        var json = new MediaDataService().Serialize(new ObservableCollection<MediaNode> { node });
        using var document = JsonDocument.Parse(json);

        var serializedNode = document.RootElement[0];
        var serializedItem = serializedNode.GetProperty("Items")[0];
        var serializedNodeAsset = serializedNode.GetProperty("Assets")[0];
        var serializedItemAsset = serializedItem.GetProperty("Assets")[0];

        Assert.Equal(
            "Library/Node/Cover/node.png",
            serializedNodeAsset.GetProperty("RelativePath").GetString());
        Assert.Equal(
            "Library/Games/Game/Logo/logo.png",
            serializedItemAsset.GetProperty("RelativePath").GetString());

        Assert.DoesNotContain(
            serializedNode.EnumerateObject(),
            property => property.Name.StartsWith("Primary", StringComparison.Ordinal));
        Assert.DoesNotContain(
            serializedItem.EnumerateObject(),
            property => property.Name.StartsWith("Primary", StringComparison.Ordinal));
        Assert.False(serializedItem.TryGetProperty("ItemLogoPath", out _));
        Assert.False(serializedItem.TryGetProperty("ManualAssets", out _));

        foreach (var asset in serializedItem.GetProperty("Assets").EnumerateArray())
        {
            Assert.False(asset.TryGetProperty("AbsolutePath", out _));
            Assert.False(asset.TryGetProperty("DisplayLabel", out _));
        }
    }

    [Fact]
    public async Task LoadAsync_IgnoresPreviouslySerializedComputedAssetProperties()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        File.WriteAllText(
            temp.GetPath("retromind_tree.json"),
            """
            [
              {
                "Name": "Node",
                "PrimaryCoverPath": "Library/Node/Cover/node.png",
                "PrimaryCoverAbsolutePath": "/old/root/Library/Node/Cover/node.png",
                "Assets": [
                  {
                    "Type": 0,
                    "RelativePath": "Library/Node/Cover/node.png",
                    "AbsolutePath": "/old/root/Library/Node/Cover/node.png",
                    "DisplayLabel": "node"
                  }
                ],
                "Items": [
                  {
                    "Title": "Game",
                    "PrimaryCoverPath": "/old/root/Library/Games/Game/Cover/cover.png",
                    "ItemLogoPath": "Library/Games/Game/Logo/logo.png",
                    "ManualAssets": []
                  }
                ]
              }
            ]
            """);

        var roots = await new MediaDataService().LoadAsync();

        var node = Assert.Single(roots);
        Assert.Equal("Node", node.Name);
        Assert.Equal("Library/Node/Cover/node.png", Assert.Single(node.Assets).RelativePath);
        Assert.Equal("Game", Assert.Single(node.Items).Title);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyLibraryWhenNoPersistedFilesExist()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);

        var roots = await new MediaDataService().LoadAsync();

        Assert.Empty(roots);
    }

    [Fact]
    public async Task LoadAsync_AcceptsValidEmptyPrimaryLibrary()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        File.WriteAllText(temp.GetPath("retromind_tree.json"), "[]");

        var roots = await new MediaDataService().LoadAsync();

        Assert.Empty(roots);
    }

    [Fact]
    public async Task LoadAsync_DoesNotInspectBackupWhenPrimaryLibraryIsValid()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        File.WriteAllText(temp.GetPath("retromind_tree.json"), "[]");
        Directory.CreateDirectory(temp.GetPath("retromind_tree.bak"));

        var roots = await new MediaDataService().LoadAsync();

        Assert.Empty(roots);
        Assert.True(Directory.Exists(temp.GetPath("retromind_tree.bak")));
    }

    [Fact]
    public async Task LoadAsync_UsesBackupWhenPrimaryIsCorrupt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new MediaDataService();
        var backupJson = service.Serialize(
            new ObservableCollection<MediaNode>
            {
                new() { Name = "Recovered" }
            });
        File.WriteAllText(temp.GetPath("retromind_tree.json"), "{ invalid");
        File.WriteAllText(temp.GetPath("retromind_tree.bak"), backupJson);

        var roots = await service.LoadAsync();

        Assert.Equal("Recovered", Assert.Single(roots).Name);
        Assert.False(File.Exists(temp.GetPath("retromind_tree.json")));
        Assert.Single(Directory.EnumerateFiles(
            temp.RootPath,
            "retromind_tree.json.corrupt-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LoadAsync_ThrowsAndDoesNotCreateEmptyPrimaryWhenBothFilesAreCorrupt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var backupPath = temp.GetPath("retromind_tree.bak");
        File.WriteAllText(temp.GetPath("retromind_tree.json"), "{ invalid primary");
        File.WriteAllText(backupPath, "{ invalid backup");

        var exception = await Assert.ThrowsAsync<LibraryLoadException>(
            () => new MediaDataService().LoadAsync());

        Assert.IsType<JsonException>(exception.PrimaryError);
        Assert.IsType<JsonException>(exception.BackupError);
        Assert.False(File.Exists(temp.GetPath("retromind_tree.json")));
        Assert.Equal("{ invalid backup", File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenPrimaryPathCannotBeReadAsAFile()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        Directory.CreateDirectory(temp.GetPath("retromind_tree.json"));

        var exception = await Assert.ThrowsAsync<LibraryLoadException>(
            () => new MediaDataService().LoadAsync());

        Assert.NotNull(exception.PrimaryError);
        Assert.True(Directory.Exists(temp.GetPath("retromind_tree.json")));
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
