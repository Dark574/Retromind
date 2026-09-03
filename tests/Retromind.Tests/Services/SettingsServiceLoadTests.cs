using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class SettingsServiceLoadTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaultsAndAllowsSavingWhenNoPersistedFilesExist()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();

        var settings = await service.LoadAsync();
        settings.ItemWidth = 222;
        await service.SaveAsync(settings);

        Assert.False(service.HasLoadFailure);
        Assert.True(File.Exists(temp.GetPath("app_settings.json")));
    }

    [Fact]
    public async Task LoadAsync_DoesNotInspectBackupWhenPrimarySettingsAreValid()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        File.WriteAllText(
            temp.GetPath("app_settings.json"),
            service.Serialize(new AppSettings { ItemWidth = 222 }));
        Directory.CreateDirectory(temp.GetPath("app_settings.json.bak"));

        var settings = await service.LoadAsync();

        Assert.Equal(222, settings.ItemWidth);
        Assert.False(service.HasLoadFailure);
        Assert.True(Directory.Exists(temp.GetPath("app_settings.json.bak")));
    }

    [Fact]
    public async Task LoadAsync_UsesBackupAndRestoresPrimaryWhenPrimaryIsCorrupt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        File.WriteAllText(temp.GetPath("app_settings.json"), "{ invalid primary");
        File.WriteAllText(
            temp.GetPath("app_settings.json.bak"),
            service.Serialize(new AppSettings { ItemWidth = 222 }));

        var settings = await service.LoadAsync();

        Assert.Equal(222, settings.ItemWidth);
        Assert.False(service.HasLoadFailure);
        Assert.Single(Directory.EnumerateFiles(
            temp.RootPath,
            "app_settings.json.corrupt_*",
            SearchOption.TopDirectoryOnly));

        using var restoredJson = JsonDocument.Parse(File.ReadAllText(temp.GetPath("app_settings.json")));
        Assert.Equal(222, restoredJson.RootElement.GetProperty(nameof(AppSettings.ItemWidth)).GetDouble());
    }

    [Fact]
    public async Task LoadAsync_ThrowsAndBlocksWritesWhenBothFilesAreCorrupt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var backupPath = temp.GetPath("app_settings.json.bak");
        File.WriteAllText(temp.GetPath("app_settings.json"), "{ invalid primary");
        File.WriteAllText(backupPath, "{ invalid backup");

        var exception = await Assert.ThrowsAsync<SettingsLoadException>(() => service.LoadAsync());

        Assert.IsType<JsonException>(exception.PrimaryError);
        Assert.IsType<JsonException>(exception.BackupError);
        Assert.True(service.HasLoadFailure);
        Assert.Same(exception, service.LoadFailure);
        Assert.False(File.Exists(temp.GetPath("app_settings.json")));
        Assert.Equal("{ invalid backup", File.ReadAllText(backupPath));

        var saveException = await Assert.ThrowsAsync<SettingsLoadException>(
            () => service.SaveAsync(new AppSettings()));
        Assert.Same(exception, saveException);
        Assert.False(File.Exists(temp.GetPath("app_settings.json")));
        Assert.Equal("{ invalid backup", File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task LoadAsync_ThrowsAndBlocksWritesWhenPrimaryPathCannotBeReadAsAFile()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var primaryPath = temp.GetPath("app_settings.json");
        Directory.CreateDirectory(primaryPath);

        var exception = await Assert.ThrowsAsync<SettingsLoadException>(() => service.LoadAsync());

        Assert.NotNull(exception.PrimaryError);
        Assert.True(service.HasLoadFailure);
        await Assert.ThrowsAsync<SettingsLoadException>(() => service.SaveJsonAsync("{}"));
        Assert.True(Directory.Exists(primaryPath));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenOnlyPersistedBackupIsCorrupt()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        var backupPath = temp.GetPath("app_settings.json.bak");
        File.WriteAllText(backupPath, "{ invalid backup");

        var exception = await Assert.ThrowsAsync<SettingsLoadException>(() => service.LoadAsync());

        Assert.Null(exception.PrimaryError);
        Assert.IsType<JsonException>(exception.BackupError);
        Assert.True(service.HasLoadFailure);
        Assert.False(File.Exists(temp.GetPath("app_settings.json")));
        Assert.Equal("{ invalid backup", File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task LoadAsync_RemainsFailedAfterOnlyCorruptPrimaryWasQuarantined()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var service = new SettingsService();
        File.WriteAllText(temp.GetPath("app_settings.json"), "{ invalid primary");

        var firstException = await Assert.ThrowsAsync<SettingsLoadException>(() => service.LoadAsync());
        var secondException = await Assert.ThrowsAsync<SettingsLoadException>(() => service.LoadAsync());

        Assert.Same(firstException, secondException);
        Assert.False(File.Exists(temp.GetPath("app_settings.json")));
        Assert.False(File.Exists(temp.GetPath("app_settings.json.bak")));
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
