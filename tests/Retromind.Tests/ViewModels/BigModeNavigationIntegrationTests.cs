using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Retromind.Extensions;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeNavigationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ControllerKeyboardAndPages_UseOneCursorAndPreserveFrameAndOwner() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var child = new MediaNode("Child");
            var root = new MediaNode("Mixed") { Children = [child],
                Items = new(Enumerable.Range(0, 55).Select(i => new MediaItem($"Media {i}"))) };
            using var fixture = new BigModeNavigationFixture([root]);
            var vm = fixture.Vm;
            var themeChanges = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == "ThemeContextNode") themeChanges++; };
            vm.PlayCurrentCommand.Execute(null);
            Assert.Same(root, vm.ThemeContextNode);
            Assert.Same(child, vm.SelectedCategory);
            var entries = vm.NavigationEntries;
            var media = vm.Items;
            vm.NavigateFromKeyboard(GamepadService.GamepadDirection.Right);
            Assert.Same(root.Items[0], vm.SelectedItem);
            Assert.Null(vm.SelectedCategory);
            Assert.Equal(0, vm.SelectedItemIndex);
            Assert.Equal(1, vm.SelectedEntryIndex);
            Assert.Equal(1, themeChanges);
            vm.NavigatePage(1);
            Assert.True(vm.SelectedEntryIndex > 1);
            Assert.Same(entries, vm.NavigationEntries);
            Assert.Same(media, vm.Items);
            fixture.Session.TrySelect(0);
            vm.PlayCurrentCommand.Execute(null);
            Assert.Same(child, vm.CurrentNode);
            Assert.True(vm.IsNavigationEmpty);
            vm.ExitBigModeCommand.Execute(null);
            Assert.Same(root, vm.CurrentNode);
            Assert.Same(child, vm.SelectedCategory);
        });

    [Theory]
    [InlineData(NavigationPresentation.MediaItemsOnly)]
    [InlineData(NavigationPresentation.LegacySeparate)]
    public async Task SpecializedAndLegacyPresentations_ExposeAllEntriesWithoutHome(NavigationPresentation mode) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Direct");
            var root = new MediaNode("Root") { Children = [new("Child")], Items = [item] };
            using var fixture = new BigModeNavigationFixture([root], mode);
            fixture.Session.ActivateSelected();
            var vm = fixture.Vm;
            Assert.False(vm.SupportsHome);
            Assert.True(vm.ShowHostNavigation);
            Assert.Equal(2, vm.NavigationEntries.Count);
            vm.SelectNextCommand.Execute(null);
            Assert.Same(item, vm.SelectedItem);
            Assert.True(vm.ShowHostNavigation);
            Assert.All(vm.CircularItems, value => Assert.IsType<MediaItem>(value));
            Assert.Single(vm.Items);
            vm.SelectedItem = null; // a legacy list reset must not clear the canonical selection
            Assert.Same(item, vm.SelectedItem);
            vm.SelectPreviousCommand.Execute(null);
            Assert.NotNull(vm.SelectedCategory);
            Assert.True(vm.ShowHostNavigation);
        });

    [Fact]
    public async Task HomeRoundTripAndMixedLibrary_KeepFullLibraryBookmarkAndActualMediaOwner()
    {
        Task? transition = null;
        BigModeNavigationFixture? fixture = null;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var direct = new MediaItem("Direct") { IsFavorite = true };
            var child = new MediaNode("Child");
            var root = new MediaNode("Root") { Children = [child], Items = [direct] };
            fixture = new BigModeNavigationFixture([root], home: true);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1);
            transition = fixture.CallAsync("ToggleHomeAsync");
        });
        await transition!;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.True(fixture!.Vm.IsHomeActive);
            Assert.Contains(fixture.Vm.HomeSections.SelectMany(s => s.Entries), e => e.Item != null && ReferenceEquals(e.SourceNode, fixture.Vm.CurrentNode));
            transition = fixture.CallAsync("ToggleHomeAsync");
        });
        await transition!;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.False(fixture!.Vm.IsHomeActive);
            Assert.IsType<BigModeMediaNavigationEntry>(fixture.Vm.SelectedEntry);
            Assert.Same(fixture.Vm.CurrentNode, fixture.Vm.ThemeContextNode);
            Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
            fixture.Dispose();
        });
    }

    [Fact]
    public async Task LibraryEditsAndProtection_AreFlushedBeforeActivationAndCollectionsCanBeReplaced() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Removed");
            var root = new MediaNode("Root") { Items = [item], Children = [new("Empty")] };
            using var fixture = new BigModeNavigationFixture([root], parentalFilter: true);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1);
            item.IsProtected = true;
            fixture.Call("FlushNavigationChanges");
            Assert.Null(fixture.Vm.SelectedItem);
            Assert.Single(fixture.Vm.NavigationEntries);
            root.Items = [new("Replacement")];
            fixture.Call("FlushNavigationChanges");
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.Equal("Replacement", fixture.Vm.SelectedItem!.Title);
            fixture.Vm.SetParentalFilterActive(false);
            fixture.Vm.SaveState();
            Assert.Equal(root.Id, fixture.Settings.LastSelectedNodeId);
        });

    [Fact]
    public async Task LargeMixedLibrary_SelectionPreservesCollectionsAndWheelRecyclesWithoutReset() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Large") { Children = new(Enumerable.Range(0, 500).Select(i => new MediaNode($"Node {i}"))),
                Items = new(Enumerable.Range(0, 50000).Select(i => new MediaItem($"Item {i}"))) };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(25000);
            var vm = fixture.Vm;
            var entries = vm.NavigationEntries;
            var media = vm.Items;
            var initialWheelIndex = vm.WheelSelectedEntryIndex; // binding order must not matter
            var wheel = vm.WheelEntries;
            var resets = 0;
            wheel.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            var version = fixture.Session.LibraryVersion;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 2000; i++) vm.SelectNextCommand.Execute(null);
            watch.Stop();
            output.WriteLine($"2000 production VM selection changes / 50500 entries: {watch.Elapsed.TotalMilliseconds:F1} ms (CPU renderer, no VLC/window).");
            Assert.Same(entries, vm.NavigationEntries);
            Assert.Same(media, vm.Items);
            Assert.Equal(version, fixture.Session.LibraryVersion);
            Assert.Equal(0, resets);
            Assert.InRange(wheel.Count, 5, 9);
            Assert.Equal(27000, vm.SelectedEntryIndex);
        });

    [Fact]
    public async Task ActivationAfterPendingProtectionEdit_DoesNotLaunchTheReplacementSelection() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var stale = new MediaItem("Stale");
            var replacement = new MediaItem("Replacement");
            var root = new MediaNode("Root") { Items = [stale, replacement] };
            using var fixture = new BigModeNavigationFixture([root], parentalFilter: true);
            fixture.Session.ActivateSelected();
            MediaItem? launched = null;
            fixture.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
            stale.IsProtected = true;
            fixture.Vm.PlayCurrentCommand.Execute(null);
            Assert.Null(launched);
            Assert.Same(replacement, fixture.Vm.SelectedItem);
            fixture.Vm.PlayCurrentCommand.Execute(null);
            Assert.Same(replacement, launched);
        });

    [Fact]
    public async Task MediaActivation_UsesExistingRequestAndThemeReplacementKeepsCursor() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Direct");
            var root = new MediaNode("Root") { Children = [new("Child")], Items = [item] };
            using var fixture = new BigModeNavigationFixture([root]);
            var vm = fixture.Vm;
            MediaItem? launched = null;
            vm.RequestPlay += selected => { launched = selected; return Task.CompletedTask; };
            vm.PlayCurrentCommand.Execute(null);
            Assert.Null(launched); // opening a node only navigates
            vm.SelectNextCommand.Execute(null);
            vm.PlayCurrentCommand.Execute(null);
            Assert.Same(item, launched);
            var entries = vm.NavigationEntries;
            var selection = vm.SelectedEntry;
            vm.UpdateTheme(new Theme(new Avalonia.Controls.Border(), new ThemeSounds(), AppContext.BaseDirectory,
                primaryVideoEnabled: false, secondaryVideoEnabled: false,
                navigationPresentation: NavigationPresentation.MediaItemsOnly));
            Assert.Same(entries, vm.NavigationEntries);
            Assert.Same(selection, vm.SelectedEntry);
            Assert.True(vm.ShowHostNavigation);
            Assert.Same(root, vm.ThemeContextNode);
        });

    [Fact]
    public async Task MixedSelection_UsesSelectedNodeOrRealMediaOwnerForArtworkVideoAndAchievements()
    {
        using var directory = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")), ("APPDIR", null));
        directory.CreateFile("Library/owner.mp4");
        directory.CreateFile("Library/child.mp4");
        directory.CreateFile("Library/item.mp4");
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var inherited = new MediaItem("Inherited") { RetroAchievementsGame = new() { GameId = 123 } };
            var own = new MediaItem("Own") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/item.mp4" },
                new() { Type = AssetType.Cover, RelativePath = "Library/item.jpg" }] };
            var child = new MediaNode("Child") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/child.mp4" }] };
            var root = new MediaNode("Root") { Children = [child], Items = [inherited, own], VideoFallbackEnabled = true,
                LogoFallbackEnabled = true, Assets = [new() { Type = AssetType.Video, RelativePath = "Library/owner.mp4" },
                    new() { Type = AssetType.Logo, RelativePath = "Library/owner.png" }] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Settings.RetroAchievements.Enabled = true;
            fixture.Settings.RetroAchievements.ShowInBigMode = true;
            fixture.Session.ActivateSelected();
            string? Preview() => (string?)typeof(Retromind.ViewModels.BigModeViewModel)
                .GetMethod("ResolvePreviewVideoPath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(fixture.Vm, null);
            Assert.Equal(directory.GetPath("Library/child.mp4"), Preview());
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.Equal(directory.GetPath("Library/owner.mp4"), Preview());
            Assert.Equal(directory.GetPath("Library/owner.png"), fixture.Vm.ActiveLogoPath);
            Assert.Same(inherited, fixture.Get<MediaItem?>("_pendingRetroAchievementsItem"));
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.Equal(directory.GetPath("Library/item.mp4"), Preview());
            Assert.Equal(directory.GetPath("Library/item.jpg"), fixture.Vm.SelectedEntry!.CoverPath);
            fixture.Vm.SelectedEntryIndex = 0;
            Assert.Null(fixture.Get<MediaItem?>("_pendingRetroAchievementsItem"));
            Assert.Same(root, fixture.Vm.ThemeContextNode);
        });
    }

    [Fact]
    public async Task StartupSanitizer_PreservesLegacyMixedSelectionAndDesktopSyncCannotOverwriteBookmark() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var item = new MediaItem("Direct");
            var root = new MediaNode("Root") { Children = [new("Child")], Items = [item] };
            var settings = new AppSettings { LastSelectedNodeId = root.Id, LastSelectedMediaId = item.Id,
                LastBigModeNavigationPath = [root.Id], LastBigModeSelectedNodeId = item.Id, LastBigModeWasItemView = true };
            var type = typeof(Retromind.ViewModels.MainWindowViewModel);
            var main = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Set(string field, object value) => type.GetField(field, flags)!.SetValue(main, value);
            Set("_currentSettings", settings);
            Set("_rootItems", new ObservableCollection<MediaNode> { root });
            Assert.True((bool)type.GetMethod("SanitizeSelectionState", flags)!.Invoke(main, null)!);
            var bookmark = settings.BigModeNavigation;
            Assert.Equal(item.Id, bookmark!.SelectedMediaId);
            Set("_synchronizingSelectionFromBigMode", true);
            type.GetMethod("UpdateBigModeStateFromCoreSelection", flags)!.Invoke(main, [root, null]);
            Assert.Same(bookmark, settings.BigModeNavigation);
            Set("_synchronizingSelectionFromBigMode", false);
            Set("_isLibraryLoading", true);
            type.GetMethod("UpdateBigModeStateFromCoreSelection", flags)!.Invoke(main, [root, null]);
            Assert.Same(bookmark, settings.BigModeNavigation);
        });

    [Fact]
    public async Task Attract_SelectsOnlyDirectMediaAndUserNavigationCancelsPendingSpin()
    {
        BigModeNavigationFixture? fixture = null;
        Task? spin = null;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Mixed") { Children = [new("Child")], Items = [new("A"), new("B")] };
            fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            fixture.Call("SelectAttractMedia", 1);
            Assert.Equal("B", fixture.Vm.SelectedItem!.Title);
            Assert.Same(root, fixture.Vm.CurrentNode);
            spin = fixture.CallAsync("PerformAttractModeStepAnimatedAsync");
        });
        await Task.Delay(30);
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            fixture!.Vm.SelectedEntry = fixture.Vm.NavigationEntries[0];
            Assert.False(fixture.Vm.IsInAttractMode);
        });
        await spin!;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.IsType<BigModeNodeNavigationEntry>(fixture!.Vm.SelectedEntry);
            Assert.Equal(1, fixture.Session.Depth);
            fixture.Dispose();
        });
    }
}
