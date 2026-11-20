using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopStoreWorkflowFactoryTests
{
    [Fact]
    public void ConfiguredStoreWorkflowsShareDesktopOperationAndSelectionGuards()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var settings = new AppSettings();
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<SettingsService>();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var busy = false;
        var multiSelect = false;
        var roots = new ObservableCollection<MediaNode>();
        var workspace = new DesktopStoreWorkspace(
            settings,
            GetWindow: () => null,
            GetStorageProvider: () => null,
            GetRootItems: () => roots,
            GetMultiSelectionActive: () => multiSelect,
            GetOperationInProgress: () => busy,
            SetOperationInProgress: value => busy = value,
            FindParentNode: _ => null,
            FindInheritedEmulator: _ => null,
            SaveLibraryAsync: () => Task.FromResult(true),
            MarkLibraryDirty: () => { },
            MarkLibraryDirtyAndSaveSoon: () => { },
            RefreshActions: () => { },
            RefreshItemAsync: _ => Task.CompletedTask,
            PublishImportAsync: (_, _) => Task.CompletedTask);
        var (gog, epic, downloads) = provider.GetRequiredService<DesktopStoreWorkflowFactory>().Create(workspace);
        Assert.NotNull(downloads);
        var game = new MediaItem("Unlinked game");

        Assert.True(gog.CanLinkMediaItemToGog(game));
        Assert.True(epic.CanLinkMediaItemToEpic(game));

        busy = true;
        Assert.False(gog.CanLinkMediaItemToGog(game));
        Assert.False(epic.CanLinkMediaItemToEpic(game));

        busy = false;
        multiSelect = true;
        Assert.False(gog.CanLinkMediaItemToGog(game));
        Assert.False(epic.CanLinkMediaItemToEpic(game));
    }
}
