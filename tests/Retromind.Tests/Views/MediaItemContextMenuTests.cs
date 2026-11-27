using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Retromind.Views;

namespace Retromind.Tests.Views;

public sealed class MediaItemContextMenuTests
{
    [Fact]
    public async Task TilesAndSuppressedRequests_DoNotConstructAMenu()
    {
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var node = new MediaNode { Name = "Library" };
            using var vm = new MediaAreaViewModel(node, 300) { IsMultiSelectMode = true };
            var view = new UserControl { DataContext = vm };
            var controller = new MediaItemContextMenuController(view, new ListBox());
            for (var i = 0; i < 500; i++)
            {
                var request = new ContextRequestedEventArgs();
                controller.Request(new Border { DataContext = new MediaItem() }, request);
                Assert.True(request.Handled);
                Assert.Null(controller.Menu);
            }
            Assert.Null(vm.SelectedMediaItem);
        });
    }

    [Fact]
    public async Task Reopen_ReusesTheDeclarativeMenuAndRebindsItemAndCategory()
    {
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var view = new UserControl();
            var controller = new MediaItemContextMenuController(view, new ListBox());
            MediaItem? edited = null;
            var commands = Commands(item => edited = item);
            var first = new MediaItem { Title = "First" };
            var second = new MediaItem { Title = "Second" };
            var node = new MediaNode { Name = "Category" };
            var tile = new Border { DataContext = first };
            var menu = controller.Prepare(tile, new MediaItemMenuContext(commands, first, node));
            var edit = Items(menu).Single(i => ReferenceEquals(i.Command, commands.EditMediaCommand));
            var add = Items(menu).Single(i => ReferenceEquals(i.Command, commands.AddMediaCommand));
            Assert.Same(first, edit.CommandParameter);
            Assert.Same(node, add.CommandParameter);
            Assert.True(add.IsVisible);
            Assert.Equal(31, Items(menu).Count());

            controller.Close();
            Assert.Null(menu.DataContext);
            Assert.Null(edit.CommandParameter);
            Assert.Null(edit.Command);

            tile.DataContext = second;
            Assert.Same(menu, controller.Prepare(tile, new MediaItemMenuContext(commands, second, null)));
            Assert.Same(second, edit.CommandParameter);
            Assert.False(add.IsVisible); // The search view retains its smaller action set.
            edit.Command!.Execute(edit.CommandParameter);
            Assert.Same(second, edited);
            controller.Close();
        });
    }

    [Fact]
    public async Task RecyclingAndViewChanges_ReleaseScopesAndUnsubscribeOldTargets()
    {
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var view = new UserControl();
            var controller = new MediaItemContextMenuController(view, new ListBox());
            var first = new MediaItem();
            var second = new MediaItem();
            var oldTile = new Border { DataContext = first };
            var newTile = new Border { DataContext = second };
            var commands = Commands();
            var menu = controller.Prepare(oldTile, new MediaItemMenuContext(commands, first, null));
            oldTile.DataContext = second;
            Assert.Null(menu.DataContext);
            controller.Prepare(newTile, new MediaItemMenuContext(commands, second, null));
            oldTile.DataContext = first;
            Assert.Same(second, ((MediaItemMenuContext)menu.DataContext!).Item);
            view.DataContext = new object();
            Assert.Null(controller.Menu);
            Assert.Null(menu.DataContext);
            Assert.All(Items(menu), i => Assert.Null(i.CommandParameter));
        });
    }

    [Fact]
    public async Task SelectionAndMultiSelectChanges_ClearTheActiveScope()
    {
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var first = new MediaItem("First");
            var second = new MediaItem("Second");
            var node = new MediaNode { Items = [first, second] };
            using var vm = new MediaAreaViewModel(node, 300) { SelectedMediaItem = first };
            var view = new UserControl { DataContext = vm };
            var controller = new MediaItemContextMenuController(view, new ListBox());
            var menu = controller.Prepare(new Border { DataContext = first }, new MediaItemMenuContext(Commands(), first, node));
            vm.IsMultiSelectMode = true;
            Assert.Null(menu.DataContext);
            vm.IsMultiSelectMode = false;
            vm.SelectedMediaItem = first;
            controller.Prepare(new Border { DataContext = first }, new MediaItemMenuContext(Commands(), first, node));
            vm.SelectedMediaItem = second;
            Assert.Null(menu.DataContext);
        });
    }

    [Fact]
    public async Task StoreStateBindings_UpdateWithoutRebuildingOrReopeningTheMenu()
    {
        using var temp = new TemporaryDirectory();
        var installDirectory = temp.CreateDirectory("installed");
        var launcher = temp.CreateFile("installed/game.sh");
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Game");
            var commands = Commands();
            var controller = new MediaItemContextMenuController(new UserControl(), new ListBox());
            var menu = controller.Prepare(new Border { DataContext = item }, new MediaItemMenuContext(commands, item, null));
            var install = Items(menu).Single(i => ReferenceEquals(i.Command, commands.InstallStoreItemCommand));
            var gogUpdate = Items(menu).Single(i => ReferenceEquals(i.Command, commands.UpdateGogCommand));
            var epicUpdate = Items(menu).Single(i => ReferenceEquals(i.Command, commands.UpdateEpicCommand));
            var store = menu.Items.OfType<MenuItem>().Single(i => i.Items.Contains(install));

            item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "steam";
            item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
            item.NotifyStorePresentationChanged();
            Assert.False(store.IsVisible);
            item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
            item.NotifyStorePresentationChanged();
            Assert.True(store.IsVisible);
            Assert.True(install.IsVisible);
            Assert.False(gogUpdate.IsVisible);
            item.LauncherPath = launcher;
            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = installDirectory;
            item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "true";
            item.NotifyStorePresentationChanged();
            Assert.True(gogUpdate.IsVisible);
            Assert.False(epicUpdate.IsVisible);
            item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "epic";
            item.NotifyStorePresentationChanged();
            Assert.False(gogUpdate.IsVisible);
            Assert.True(epicUpdate.IsVisible);
            item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "false";
            item.NotifyStorePresentationChanged();
            Assert.False(epicUpdate.IsVisible);
            Assert.Same(menu, controller.Menu);
            controller.Close();
        });
    }

    private static MainWindowViewModel Commands(Action<MediaItem?>? onEdit = null)
    {
        // Commands are explicit properties; no application services or native
        // window are needed to exercise the real XAML bindings and parameters.
        var commands = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.EditMediaCommand))!
            .SetValue(commands, new AsyncRelayCommand<MediaItem?>(item =>
            {
                onEdit?.Invoke(item);
                return Task.CompletedTask;
            }));
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.AddMediaCommand))!
            .SetValue(commands, new AsyncRelayCommand<MediaNode?>(_ => Task.CompletedTask));
        foreach (var name in new[] { "InstallStoreItemCommand", "UpdateGogCommand", "UpdateEpicCommand" })
            typeof(MainWindowViewModel).GetProperty(name)!
                .SetValue(commands, new AsyncRelayCommand<MediaItem?>(_ => Task.CompletedTask));
        return commands;
    }

    private static IEnumerable<MenuItem> Items(ItemsControl root)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Items(item))
                yield return child;
        }
    }
}
