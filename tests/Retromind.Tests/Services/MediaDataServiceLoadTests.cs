using System.Collections.ObjectModel;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class MediaDataServiceLoadTests
{
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
