using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Retromind.Extensions;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Tests.TestInfrastructure;
using Retromind.Views;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeThemeNavigationTests
{
    [Theory]
    [InlineData("Default", NavigationPresentation.MixedEntries)]
    [InlineData("Prism", NavigationPresentation.MixedEntries)]
    [InlineData("HorizontalRow", NavigationPresentation.MixedEntries)]
    [InlineData("ArchiveAtlas", NavigationPresentation.MixedEntries)]
    [InlineData("Wheel", NavigationPresentation.MixedEntries)]
    [InlineData("Arcade", NavigationPresentation.MediaItemsOnly)]
    [InlineData("LivingRoom", NavigationPresentation.MediaItemsOnly)]
    [InlineData("System", NavigationPresentation.NodeBrowser)]
    public async Task ShippedRoots_ParseAndBindBothKindsWithTheirDeclaredPresentation(string name, NavigationPresentation expected) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var view = Parse(name);
            Assert.Equal(expected, ThemeProperties.GetNavigationPresentation(view));
            var child = new MediaNode("Child");
            var root = new MediaNode("Mixed") { Children = [child], Items = [new("Direct")] };
            using var fixture = new BigModeNavigationFixture([root], expected);
            fixture.Session.ActivateSelected();
            view.DataContext = fixture.Vm;
            var lists = view.GetLogicalDescendants().OfType<ListBox>().ToArray();
            if (expected != NavigationPresentation.MediaItemsOnly)
            {
                var list = Assert.Single(lists);
                Assert.Equal(2, list.ItemCount);
                Assert.Contains(list.Items, entry => entry is BigModeNodeNavigationEntry);
                Assert.Contains(list.Items, entry => entry is BigModeMediaNavigationEntry);
                if (name != "Wheel") Assert.Same(fixture.Vm.SelectedEntry, list.SelectedItem);
            }
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.Equal("Direct", fixture.Vm.SelectedItem!.Title);
            if (expected is NavigationPresentation.MixedEntries or NavigationPresentation.NodeBrowser && name != "Wheel")
                Assert.Same(fixture.Vm.SelectedEntry, Assert.Single(lists).SelectedItem);
            if (expected == NavigationPresentation.MediaItemsOnly) Assert.True(fixture.Vm.ShowHostNavigation);
            view.DataContext = null;
        });

    [Theory]
    [InlineData("Default")]
    [InlineData("Amiga")]
    [InlineData("C64")]
    [InlineData("GBA")]
    [InlineData("MAME")]
    [InlineData("MegaDrive")]
    [InlineData("PC")]
    [InlineData("PS1")]
    [InlineData("PS2")]
    [InlineData("SNES")]
    public async Task SystemPresentations_KeepFixedNodeContextWithoutLibraryNavigation(string name) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var view = Parse("System/" + name);
            var node = new MediaNode("Preview");
            ThemeProperties.SetSystemPreviewNode(view, node);
            Assert.Same(node, ThemeProperties.GetSystemPreviewNode(view));
            Assert.Empty(view.GetLogicalDescendants().OfType<ListBox>());
        });

    [Fact]
    public async Task SystemHost_DoesNotClearEntriesAndUsesMediaPreviewForMediaSelection() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Movie");
            var root = new MediaNode("Mixed") { Children = [new("Child")], Items = [item] };
            using var fixture = new BigModeNavigationFixture([root], NavigationPresentation.NodeBrowser);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1);
            var entries = fixture.Vm.NavigationEntries;
            var view = Parse("System");
            ManagedCursorFactory.EnsureRegistered();
            var host = new BigModeHostView { DataContext = fixture.Vm };
            var theme = new Theme(view, new ThemeSounds(), AppContext.BaseDirectory,
                navigationPresentation: NavigationPresentation.NodeBrowser);
            host.SetThemeContent(view, theme);
            var slot = view.FindControl<ContentControl>("SystemLayoutHost")!;
            Assert.IsType<BigModeMediaPreviewView>(slot.Content);
            Assert.Same(entries, fixture.Vm.NavigationEntries);
            Assert.Same(item, fixture.Vm.SelectedItem);
            Assert.Single(fixture.Vm.Items);
            host.DataContext = null;
        });

    [Theory]
    [InlineData("Arcade")]
    [InlineData("LivingRoom")]
    public async Task SpecializedHostBrowser_BindsNodesAndMediaToOneCursorWithoutHome(string name) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Mixed") { Children = [new("Child")], Items = [new("Direct")] };
            using var fixture = new BigModeNavigationFixture([root], NavigationPresentation.MediaItemsOnly);
            fixture.Session.ActivateSelected();
            var view = Parse(name);
            ManagedCursorFactory.EnsureRegistered();
            var host = new BigModeHostView { DataContext = fixture.Vm };
            host.SetThemeContent(view, new Theme(view, new ThemeSounds(), AppContext.BaseDirectory,
                primaryVideoEnabled: false, secondaryVideoEnabled: false,
                navigationPresentation: NavigationPresentation.MediaItemsOnly));
            var browser = host.FindControl<ListBox>("HostLibraryList")!;
            Assert.True(host.FindControl<Border>("LibraryNavigation")!.IsVisible);
            Assert.Equal(2, browser.ItemCount);
            Assert.IsType<BigModeNodeNavigationEntry>(browser.SelectedItem);
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.IsType<BigModeMediaNavigationEntry>(browser.SelectedItem);
            Assert.Same(fixture.Vm.SelectedEntry, browser.SelectedItem);
            Assert.False(fixture.Vm.SupportsHome);
            host.DataContext = null;
            view.DataContext = null;
        });

    [Theory]
    [InlineData("Default")]
    [InlineData("Prism")]
    [InlineData("HorizontalRow")]
    [InlineData("ArchiveAtlas")]
    [InlineData("Wheel")]
    [InlineData("Arcade")]
    [InlineData("LivingRoom")]
    [InlineData("System")]
    public async Task EveryVisibleEntry_IsReachableThroughProductionNavigationInEveryPresentation(string name) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var hidden = new MediaItem("Protected") { IsProtected = true };
            var deep = new MediaNode("Deep") { Items = [new("Deep media"), hidden] };
            var child = new MediaNode("Child") { Children = [deep], Items = [new("Child media")] };
            var root = new MediaNode("Root") { Children = [child, new("Empty")], Items = [new("Root media")] };
            var view = Parse(name);
            var presentation = ThemeProperties.GetNavigationPresentation(view);
            using var fixture = new BigModeNavigationFixture([root], presentation, parentalFilter: true);
            view.DataContext = fixture.Vm;
            var vm = fixture.Vm;
            var visited = new HashSet<BigModeNavigationKey>();
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                if (index >= vm.NavigationEntries.Count)
                {
                    if (stack.Count > 0) vm.ExitBigModeCommand.Execute(null);
                    continue;
                }
                stack.Push(index + 1);
                vm.SelectedEntryIndex = index;
                var entry = vm.SelectedEntry!;
                Assert.True(visited.Add(entry.Key));
                if (presentation == NavigationPresentation.MediaItemsOnly)
                    Assert.True(vm.ShowHostNavigation || entry is BigModeMediaNavigationEntry);
                else
                    Assert.Contains(entry, Assert.Single(view.GetLogicalDescendants().OfType<ListBox>()).Items);
                if (entry is BigModeNodeNavigationEntry)
                {
                    vm.PlayCurrentCommand.Execute(null);
                    stack.Push(0);
                }
            }
            Assert.Equal(7, visited.Count); // four real nodes, three direct media
            Assert.DoesNotContain(new BigModeNavigationKey(BigModeNavigationEntryKind.MediaItem, hidden.Id), visited);
            view.DataContext = null;
        });

    private static Control Parse(string name) => AvaloniaRuntimeXamlLoader.Parse<Control>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", name, "theme.axaml")));
}
