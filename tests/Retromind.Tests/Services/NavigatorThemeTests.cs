using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Retromind.Views;
using SkiaSharp;

namespace Retromind.Tests.Services;

public sealed class NavigatorThemeTests
{
    private static string ThemeFile => Path.Combine(AppContext.BaseDirectory, "Themes", "Navigator", "theme.axaml");
    private static Control Parse() => AvaloniaRuntimeXamlLoader.Parse<Control>(File.ReadAllText(ThemeFile));

    [Fact]
    public async Task DiscoveryAndRuntimeLoading_ExposeSharedHostCapabilities()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateFile("Themes/Navigator/theme.axaml", File.ReadAllText(ThemeFile));
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Contains("Navigator/theme.axaml", ThemeDiscovery.GetAvailableThemePaths());
            var theme = ThemeLoader.LoadTheme("Navigator/theme.axaml");
            Assert.Equal("Navigator", theme.Name);
            Assert.Equal("0.1.0", theme.Version);
            Assert.Equal(NavigationPresentation.MixedEntries, theme.NavigationPresentation);
            Assert.True(theme.SupportsHome);
            Assert.True(theme.PrimaryVideoEnabled);
            Assert.False(theme.SecondaryVideoEnabled);
            Assert.True(theme.AttractModeEnabled);
            Assert.NotNull(theme.View.FindControl<Border>(theme.VideoSlotName));
            Assert.NotSame(theme.View, theme.CreateView());
            Assert.Equal("Uniform", ThemeProperties.GetVideoStretchMode(theme.View));
            Assert.True(ThemeProperties.GetProvidesSystemMenuHint(theme.View));
        });
    }

    [Fact]
    public async Task MixedList_OpensNodesLaunchesMediaAndRestoresCanonicalSelection() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var child = new MediaNode("Child") { Description = "Node description" };
            var description = string.Join('\n', Enumerable.Repeat("Complete original description", 20));
            var item = new MediaItem("Media") { Description = description, IsFavorite = true };
            var root = new MediaNode("Mixed") { Children = [child], Items = [item, new("Hidden") { IsProtected = true }] };
            using var fixture = new BigModeNavigationFixture([root], parentalFilter: true);
            fixture.Vm.PlayCurrentCommand.Execute(null);
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var list = view.FindControl<ListBox>("NavigatorList")!;
                var title = view.FindControl<TextBlock>("SelectionTitle")!;
                var details = view.FindControl<TextBlock>("SelectionDescription")!;
                Assert.Equal(2, list.ItemCount);
                Assert.IsType<BigModeNodeNavigationEntry>(list.Items[0]);
                Assert.IsType<BigModeMediaNavigationEntry>(list.Items[1]);
                Assert.Equal(child.Name, title.Text);
                Assert.Equal(child.Description, details.Text);
                MediaItem? launched = null;
                fixture.Vm.RequestPlay += selected => { launched = selected; return Task.CompletedTask; };
                fixture.Vm.PlayCurrentCommand.Execute(null);
                Assert.Null(launched);
                Assert.Same(child, fixture.Vm.CurrentNode);
                Assert.True(view.FindControl<TextBlock>("EmptyLibrary")!.IsVisible);
                Assert.Null(title.Text);
                fixture.Vm.ExitBigModeCommand.Execute(null);
                Assert.Same(child, fixture.Vm.SelectedCategory);
                Assert.Same(fixture.Vm.SelectedEntry, list.SelectedItem);
                list.SelectedItem = fixture.Vm.NavigationEntries[1];
                Assert.Same(item, fixture.Vm.SelectedItem);
                Assert.Equal(item.Title, title.Text);
                Assert.Equal(description, details.Text);
                Assert.Equal(0, details.MaxLines);
                Assert.Same(TextTrimming.None, details.TextTrimming);
                Assert.Equal(description, item.Description);
                fixture.Vm.PlayCurrentCommand.Execute(null);
                Assert.Same(item, launched);
                Assert.Same(root, fixture.Vm.CurrentNode);
                Assert.Same(fixture.Vm.SelectedEntry, list.SelectedItem);
            }
            finally { view.DataContext = null; }
        });

    [Fact]
    public async Task Breadcrumb_UsesOpenedFramesAndUpdatesAncestorNamesWithoutCursorWork() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var child = new MediaNode("Amiga") { Items = [new("One"), new("Two")] };
            var root = new MediaNode("Games") { Children = [child] };
            using var fixture = new BigModeNavigationFixture([root]);
            var vm = fixture.Vm;
            Assert.Equal(vm.NavigationTitle, vm.NavigationBreadcrumb);
            fixture.Session.ActivateSelected();
            Assert.Equal($"{vm.NavigationTitle}  ›  Games", vm.NavigationBreadcrumb);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = vm;
            try
            {
                var label = view.FindControl<TextBlock>("Breadcrumb")!;
                Assert.Equal($"{vm.NavigationTitle}  ›  Games  ›  Amiga", label.Text);
                Assert.Same(TextTrimming.LeadingCharacterEllipsis, label.TextTrimming);
                var entries = vm.NavigationEntries;
                var version = fixture.Session.LibraryVersion;
                var updates = 0;
                vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.NavigationBreadcrumb)) updates++; };
                for (var i = 0; i < 100; i++) vm.SelectNextCommand.Execute(null);
                Assert.Equal(0, updates);
                Assert.Same(entries, vm.NavigationEntries);
                Assert.Equal(version, fixture.Session.LibraryVersion);
                root.Name = "Renamed ancestor";
                Assert.Equal($"{vm.NavigationTitle}  ›  Renamed ancestor  ›  Amiga", label.Text);
                Assert.Equal(1, updates);
                var bookmark = fixture.Session.CaptureState();
                fixture.Session.Back();
                Assert.Equal($"{vm.NavigationTitle}  ›  Renamed ancestor", label.Text);
                Assert.True(fixture.Session.RestoreState(bookmark));
                Assert.EndsWith("  ›  Amiga", label.Text);
                fixture.Session.Back();
                fixture.Session.Back();
                Assert.Equal(vm.NavigationTitle, label.Text);
            }
            finally { view.DataContext = null; }
        });

    [Fact]
    public async Task LargeLevel_UsesBoundedVerticalVirtualizationAndStableEntries() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Large") { Children = [new("Folder")],
                Items = new(Enumerable.Range(0, 10000).Select(i => new MediaItem($"Media {i}"))) };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            EnsureLayoutServices();
            view.DataContext = fixture.Vm;
            try
            {
                var list = view.FindControl<ListBox>("NavigatorList")!;
                var panel = Assert.IsType<VirtualizingStackPanel>(list.ItemsPanel!.Build());
                Assert.Equal(Orientation.Vertical, panel.Orientation);
                Assert.Equal(0.5, panel.CacheLength);
                Assert.True(ListBoxBehaviors.GetCenterSelectedItem(list));
                Assert.False(ThemeProperties.GetUseHostSelectionEffects(list));
                Layout(view, new Size(2560, 1440));
                Assert.InRange(list.Bounds.Height / 104, 9, 12);
                var entries = fixture.Vm.NavigationEntries;
                var version = fixture.Session.LibraryVersion;
                for (var i = 0; i < 100; i++) fixture.Vm.SelectNextCommand.Execute(null);
                Layout(view, new Size(2560, 1440));
                Assert.Same(entries, list.ItemsSource);
                Assert.Equal(10001, list.ItemCount);
                Assert.Same(fixture.Vm.SelectedEntry, list.SelectedItem);
                Assert.Equal(100, list.SelectedIndex);
                Assert.Equal(version, fixture.Session.LibraryVersion);
            }
            finally { view.DataContext = null; }
        });

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    public async Task AchievementAndContentChanges_KeepInformationAndPreviewInPlace(int width, int height)
    {
        BigModeNavigationFixture? fixture = null;
        Control? view = null;
        Task? selection = null;
        var identified = new MediaItem("A game") { Description = "Short description",
            RetroAchievementsGame = new RetroAchievementsGameIdentity { GameId = 123 } };
        var unidentified = new MediaItem(new string('W', 300)) { Description = new string('W', 4000) };
        var root = new MediaNode("Library") { Children = [new("Node")], Items = [identified, unidentified] };
        var size = new Size(width, height);
        Rect[]? baseline = null;
        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                fixture = new BigModeNavigationFixture([root]);
                fixture.Session.ActivateSelected();
                fixture.Vm.RetroAchievementsProgress.Dispose();
                fixture.Set("<RetroAchievementsProgress>k__BackingField", new RetroAchievementsProgressViewModel(
                    new AppSettings { RetroAchievements = new() { Enabled = true, Username = "Test" } },
                    new Progress(), new Badges()));
                fixture.Session.TrySelect(1);
                view = Parse();
                EnsureLayoutServices();
                view.DataContext = fixture.Vm;
                Layout(view, size);
                baseline = Bounds(view);
                Assert.False(view.FindControl<Grid>("AchievementSummary")!.IsVisible);
                selection = fixture.Vm.RetroAchievementsProgress.SelectItemAsync(identified);
            });
            await selection!;
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Layout(view!, size);
                var loadedView = Assert.IsAssignableFrom<Control>(view);
                var summary = loadedView.FindControl<Grid>("AchievementSummary")!;
                Assert.True(summary.IsVisible);
                Assert.True(summary.Bounds.Height > 0);
                var progress = loadedView.FindControl<ProgressBar>("AchievementProgressBar")!;
                Assert.Equal(10, progress.Maximum);
                Assert.Equal(4, progress.Value);
                Assert.Equal(baseline!, Bounds(loadedView));
                fixture!.Session.TrySelect(2);
                Layout(loadedView, size);
                Assert.False(summary.IsVisible);
                Assert.Equal(baseline!, Bounds(loadedView));
                Assert.Equal(unidentified.Description, loadedView.FindControl<TextBlock>("SelectionDescription")!.Text);
                // Even stale progress cannot appear for a node while the next selection settles.
                fixture.Session.TrySelect(0);
                selection = fixture.Vm.RetroAchievementsProgress.SelectItemAsync(identified);
            });
            await selection!;
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Assert.True(fixture!.Vm.RetroAchievementsProgress.IsVisible);
                var loadedView = Assert.IsAssignableFrom<Control>(view);
                Assert.False(loadedView.FindControl<Grid>("AchievementArea")!.IsVisible);
                Layout(loadedView, size);
                var bounds = Bounds(loadedView);
                // Metadata is hidden for nodes, but fixed grid rows keep all other sections in place.
                foreach (var index in new[] { 0, 1, 3 }) Assert.Equal(baseline![index], bounds[index]);
                var columns = loadedView.FindControl<Grid>("DetailsColumn")!;
                Assert.True(bounds[0].Bottom <= bounds[1].Top);
                Assert.True(bounds[1].Bottom <= baseline![2].Top);
                Assert.True(baseline[2].Bottom <= bounds[3].Top);
                Assert.True(bounds[3].Bottom < columns.Bounds.Height - 90);
                var viewport = loadedView.FindControl<Viewbox>("NavigatorViewport")!;
                Assert.Equal(Stretch.Uniform, viewport.Stretch);
                var preview = loadedView.FindControl<Grid>("PreviewArea")!;
                var topLeft = preview.TranslatePoint(default, viewport)!.Value;
                var bottomRight = preview.TranslatePoint(new Point(preview.Bounds.Width, preview.Bounds.Height), viewport)!.Value;
                Assert.InRange(topLeft.X, 0, width);
                Assert.InRange(topLeft.Y, 0, height);
                Assert.InRange(bottomRight.X, 0, width);
                Assert.InRange(bottomRight.Y, 0, height);
                Assert.True(bottomRight.X > topLeft.X && bottomRight.Y > topLeft.Y);
            });
        }
        finally
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ => { if (view != null) view.DataContext = null; fixture?.Dispose(); });
        }
    }

    [Fact]
    public async Task RowTemplates_DistinguishFoldersAndKeepSmallArtworkUndistorted() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = new BigModeNavigationFixture([new("Root")
                { Children = [new("Folder")], Items = [new("Media") { IsFavorite = true, Genre = "Adventure" }] }]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            var list = view.FindControl<ListBox>("NavigatorList")!;
            foreach (var entry in fixture.Vm.NavigationEntries)
            {
                var row = list.ItemTemplate!.Build(entry)!;
                row.DataContext = entry;
                var controls = row.GetLogicalDescendants().ToArray();
                Assert.All(controls.OfType<Image>(), image => Assert.Equal(Stretch.Uniform, image.Stretch));
                var fallback = controls.OfType<ContentControl>().First();
                Assert.True(fallback.IsVisible);
                var template = Assert.Single(fallback.DataTemplates, template => template.Match(entry));
                var glyph = Assert.IsType<Avalonia.Controls.Shapes.Path>(template.Build(entry));
                Assert.NotNull(glyph.Data);
                Assert.Equal(entry is BigModeNodeNavigationEntry ? 38 : 26, glyph.Width);
                Assert.True(entry.ShowCoverTitle);
                row.DataContext = null;
            }
        });

    [Theory]
    [InlineData(0)] // Item wallpaper wins.
    [InlineData(1)] // Enabled owner wallpaper fallback.
    [InlineData(2)] // Screenshot when neither wallpaper is available.
    public async Task ArtworkBindings_ResolveMediaFallbacksAndNodesOwnPreview(int fallback)
    {
        using var directory = new TemporaryDirectory();
        directory.CreateDirectory("Library");
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        using (var bitmap = new SKBitmap(16, 9))
        {
            bitmap.Erase(SKColors.Teal);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            foreach (var name in new[] { "node", "owner", "item", "screenshot" })
                File.WriteAllBytes(directory.GetPath($"Library/{name}.png"), encoded.ToArray());
        }
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var child = new MediaNode("Child") { WallpaperFallbackEnabled = false,
                Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/node.png" }] };
            var item = new MediaItem("Media");
            if (fallback == 0) item.Assets.Add(new() { Type = AssetType.Wallpaper, RelativePath = "Library/item.png" });
            if (fallback == 2) item.Assets.Add(new() { Type = AssetType.Screenshot, RelativePath = "Library/screenshot.png" });
            var root = new MediaNode("Root") { Children = [child], Items = [item], WallpaperFallbackEnabled = fallback != 2,
                Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/owner.png" }] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var node = view.FindControl<CrossfadeImage>("NodeWallpaper")!;
                var media = view.FindControl<CrossfadeImage>("MediaWallpaper")!;
                var screenshot = view.FindControl<CrossfadeImage>("ScreenshotFallback")!;
                Assert.Equal(directory.GetPath("Library/node.png"), node.Url);
                Assert.True(node.IsVisible);
                fixture.Session.TrySelect(1);
                Assert.False(node.IsVisible);
                Assert.True(media.IsVisible);
                if (fallback == 2)
                {
                    // The shared resolver already promotes an item's screenshot
                    // to wallpaper; Navigator must preserve that existing order.
                    Assert.Equal(directory.GetPath("Library/screenshot.png"), media.Url);
                    Assert.Null(screenshot.Url);
                }
                else
                {
                    Assert.Equal(directory.GetPath($"Library/{(fallback == 0 ? "item" : "owner")}.png"), media.Url);
                    Assert.Null(screenshot.Url);
                }
                fixture.Session.TrySelect(0);
                Assert.Equal(directory.GetPath("Library/node.png"), node.Url);
                Assert.Null(media.Url);
            }
            finally { view.DataContext = null; }
        });
    }

    [Fact]
    public async Task FavoriteMarker_FollowsTheTitleInGoldAndLeavesLongTitlesBounded() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            EnsureLayoutServices();
            var item = new MediaItem("3030 Deathwar Redux") { IsFavorite = true };
            using var fixture = new BigModeNavigationFixture([new("Root") { Children = [new("Folder")], Items = [item] }]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            var list = view.FindControl<ListBox>("NavigatorList")!;
            var media = fixture.Vm.NavigationEntries[1];
            var row = list.ItemTemplate!.Build(media)!;
            row.DataContext = media;
            try
            {
                var area = Assert.Single(row.GetLogicalDescendants().OfType<Grid>(), control => control.Name == "RowTitleArea");
                var title = Assert.Single(area.Children.OfType<TextBlock>());
                var marker = Assert.Single(area.Children.OfType<ContentControl>());
                var template = Assert.Single(marker.DataTemplates, template => template.Match(media));
                var star = Assert.IsType<TextBlock>(template.Build(media));
                star.DataContext = media;
                Assert.True(star.IsVisible);
                Assert.Equal(Color.Parse("#E8C56A"), Assert.IsAssignableFrom<ISolidColorBrush>(star.Foreground).Color);
                // The managed fixture has no application ContentControl template.
                // Insert its real template result to measure title/marker geometry.
                area.Children.Remove(marker);
                Grid.SetColumn(star, 1);
                area.Children.Add(star);
                Measure();
                Assert.Equal(8, star.Bounds.Left - title.Bounds.Right, 3);
                Assert.InRange(area.Bounds.Width, 1, 599);
                item.Title = new string('W', 300);
                Measure();
                Assert.True(star.Bounds.Right <= area.Bounds.Width);
                Assert.InRange(area.Bounds.Width, 1, 600);
                Assert.Equal(2, title.MaxLines);
                item.IsFavorite = false;
                Assert.False(star.IsVisible);
                var folder = fixture.Vm.NavigationEntries[0];
                var folderTemplate = Assert.Single(marker.DataTemplates, template => template.Match(folder));
                Assert.False(folderTemplate.Build(folder)!.IsVisible);

                void Measure()
                {
                    foreach (var control in area.Children.Prepend<Control>(area))
                    {
                        control.InvalidateMeasure();
                        control.InvalidateArrange();
                    }
                    area.Measure(new Size(600, 58));
                    area.Arrange(new Rect(0, 0, 600, 58));
                }
            }
            finally { row.DataContext = null; }
        });

    [Fact]
    public async Task DescriptionScroll_WaitsLoopsAndResetsForEntriesWithIdenticalText() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var description = string.Join('\n', Enumerable.Repeat("The entire original description", 20));
            using var fixture = new BigModeNavigationFixture([new("Root")
                { Children = [new("Folder") { Description = description }], Items = [new("One") { Description = description }, new("Two") { Description = description }] }]);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1);
            var view = Parse();
            view.DataContext = fixture.Vm;
            var scroll = view.FindControl<ScrollViewer>("DescriptionViewport")!;
            try
            {
                Assert.True(ScrollViewerBehaviors.GetAutoVerticalScroll(scroll));
                Assert.Equal(10000, ScrollViewerBehaviors.GetAutoVerticalScrollStartDelayMs(scroll));
                Assert.Equal(2000, ScrollViewerBehaviors.GetAutoVerticalScrollEndPauseMs(scroll));
                Assert.Equal(8, ScrollViewerBehaviors.GetAutoVerticalScrollSpeed(scroll));
                Assert.Same(fixture.Vm.SelectedEntry, ScrollViewerBehaviors.GetAutoVerticalScrollRestartToken(scroll));
                Assert.False(scroll.IsHitTestVisible);
                Assert.False(scroll.Focusable);
                var content = Assert.IsType<TextBlock>(scroll.Content);
                Assert.Equal(description, content.Text);
                Assert.Equal(0, content.MaxLines);
                Assert.Same(TextTrimming.None, content.TextTrimming);

                // Advance the existing behavior deterministically, without sleeps
                // or native scrolling. Its normal elapsed-time clamp is 250 ms.
                var state = typeof(ScrollViewerBehaviors).GetMethod("GetAutoVerticalScrollState",
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [scroll])!;
                var lastTick = state.GetType().GetField("_lastTickUtc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var tick = state.GetType().GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                typeof(ScrollViewer).GetProperty(nameof(ScrollViewer.Extent))!.SetValue(scroll, new Size(600, 180));
                typeof(ScrollViewer).GetProperty(nameof(ScrollViewer.Viewport))!.SetValue(scroll, new Size(600, 170));

                Tick(39);
                Assert.Equal(0, scroll.Offset.Y);
                Tick(1);
                Assert.Equal(2, scroll.Offset.Y, 3);
                Tick(4);
                Assert.Equal(10, scroll.Offset.Y, 3);
                Tick(7);
                Assert.Equal(10, scroll.Offset.Y, 3);
                Tick(1);
                Assert.Equal(0, scroll.Offset.Y);
                Tick(39);
                Assert.Equal(0, scroll.Offset.Y);
                Tick(1);
                Assert.True(scroll.Offset.Y > 0);

                fixture.Session.TrySelect(2);
                Assert.Equal(description, content.Text);
                Assert.Equal(0, scroll.Offset.Y);
                Tick(39);
                Assert.Equal(0, scroll.Offset.Y);
                Tick(1);
                Assert.True(scroll.Offset.Y > 0);
                fixture.Session.TrySelect(0);
                Assert.Equal(0, scroll.Offset.Y);
                Assert.Same(fixture.Vm.SelectedEntry, ScrollViewerBehaviors.GetAutoVerticalScrollRestartToken(scroll));

                typeof(ScrollViewer).GetProperty(nameof(ScrollViewer.Extent))!.SetValue(scroll, new Size(600, 100));
                Tick(60);
                Assert.Equal(0, scroll.Offset.Y);

                void Tick(int count)
                {
                    for (var i = 0; i < count; i++)
                    {
                        lastTick.SetValue(state, DateTime.UtcNow.AddMilliseconds(-250));
                        tick.Invoke(state, [null, EventArgs.Empty]);
                    }
                }
            }
            finally { ScrollViewerBehaviors.SetAutoVerticalScroll(scroll, false); view.DataContext = null; }
        });

    [Fact]
    public async Task VideoTargets_HideArtworkBeforeStartupAndKeepItHiddenBetweenVideos()
    {
        using var directory = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        directory.CreateFile("Library/first.webm", "video path only; no native playback in this test");
        directory.CreateFile("Library/second.webm", "video path only; no native playback in this test");
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Root") { Items = [new("Still artwork"),
                new("First video") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/first.webm" }] },
                new("Second video") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/second.webm" }] }] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var artwork = view.FindControl<Grid>("PreviewArtwork")!;
                Assert.False(fixture.Vm.MainVideoHasContent);
                Assert.True(artwork.IsVisible);

                // The real VM already knows the selected target during debounce,
                // before the shared video overlay has started or produced a frame.
                fixture.Session.TrySelect(1);
                Assert.True(fixture.Vm.MainVideoHasContent);
                Assert.False(fixture.Vm.IsVideoOverlayVisible);
                Assert.False(artwork.IsVisible);
                fixture.Vm.IsVideoOverlayVisible = true;
                Assert.False(artwork.IsVisible);
                fixture.Session.TrySelect(2);
                Assert.True(fixture.Vm.MainVideoHasContent);
                Assert.False(artwork.IsVisible);

                fixture.Session.TrySelect(0);
                Assert.False(fixture.Vm.MainVideoHasContent);
                fixture.Vm.IsVideoOverlayVisible = true; // An outgoing frame is still fading.
                Assert.False(artwork.IsVisible);
                fixture.Vm.IsVideoOverlayVisible = false;
                Assert.True(artwork.IsVisible);

                // A video asset must not suppress artwork when playback is disabled.
                fixture.Vm.CanShowVideo = false;
                fixture.Session.TrySelect(1);
                Assert.False(fixture.Vm.MainVideoHasContent);
                Assert.True(artwork.IsVisible);
            }
            finally { view.DataContext = null; }
        });
    }

    [Fact]
    public async Task WallpaperChange_RetainsDecodedImageDuringDebounceWithoutPlaceholderGraphic()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateDirectory("Library");
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        foreach (var name in new[] { "first", "second" })
        {
            using var bitmap = new SKBitmap(32, 18);
            bitmap.Erase(name == "first" ? SKColors.Blue : SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(directory.GetPath($"Library/{name}.png"), encoded.ToArray());
        }
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var root = new MediaNode("Root") { Items = [
                new("First") { Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/first.png" }] },
                new("Second") { Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/second.png" }] },
                new("No artwork")] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            var wallpaper = view.FindControl<CrossfadeImage>("MediaWallpaper")!;
            wallpaper.FadeDurationMs = 0;
            try
            {
                var artwork = view.FindControl<Grid>("PreviewArtwork")!;
                Assert.DoesNotContain(artwork.GetLogicalDescendants(), control => control is Avalonia.Controls.Shapes.Path);
                Assert.All(artwork.Children.OfType<CrossfadeImage>(), image => Assert.True(image.RetainCurrentImageUntilLoaded));
                WaitForImage();
                var old = Assert.Single(wallpaper.Children.OfType<Image>(), AsyncImageHelper.GetIsLoaded);
                var pixels = old.Source;
                Assert.NotNull(pixels);

                fixture.Session.TrySelect(1);
                Assert.Equal(directory.GetPath("Library/second.png"), wallpaper.Url);
                Assert.False(wallpaper.IsCurrentImageReady);
                Assert.Same(pixels, old.Source);
                Assert.True(old.IsVisible);
                Assert.Equal(1, old.Opacity);
                Assert.True(artwork.IsVisible);
                WaitForImage();
                var loaded = Assert.Single(wallpaper.Children.OfType<Image>(), image =>
                    AsyncImageHelper.GetUrl(image) == wallpaper.Url && AsyncImageHelper.GetIsLoaded(image));
                Assert.NotSame(pixels, loaded.Source);
                Assert.Equal(1, loaded.Opacity);

                fixture.Session.TrySelect(2);
                Assert.Null(wallpaper.Url);
                Assert.All(wallpaper.Children.OfType<Image>(), image => Assert.False(image.IsVisible));

                void WaitForImage()
                {
                    var timeout = Stopwatch.StartNew();
                    while (!wallpaper.IsCurrentImageReady)
                    {
                        if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Wallpaper did not become ready.");
                        dispatcher.RunJobs();
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                view.DataContext = null;
                foreach (var name in new[] { "first", "second" }) AsyncImageHelper.InvalidateCache(directory.GetPath($"Library/{name}.png"));
            }
        });
    }

    [Fact]
    public async Task VideoToWallpaper_DiscardsHiddenBuffersBeforeTheNewArtworkLoads()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateDirectory("Library");
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        directory.CreateFile("Library/preview.webm", "No native playback in this test");
        foreach (var name in new[] { "first", "video", "second" })
        {
            using var bitmap = new SKBitmap(32, 18);
            bitmap.Erase(name == "first" ? SKColors.Blue : name == "video" ? SKColors.Green : SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(directory.GetPath($"Library/{name}.png"), encoded.ToArray());
        }
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var first = new MediaItem("First wallpaper") { Assets = [
                new() { Type = AssetType.Wallpaper, RelativePath = "Library/first.png" },
                new() { Type = AssetType.Cover, RelativePath = "Library/first.png" }] };
            var video = new MediaItem("Video with its own artwork") { Assets = [
                new() { Type = AssetType.Video, RelativePath = "Library/preview.webm" },
                new() { Type = AssetType.Wallpaper, RelativePath = "Library/video.png" },
                new() { Type = AssetType.Screenshot, RelativePath = "Library/video.png" },
                new() { Type = AssetType.Cover, RelativePath = "Library/video.png" }] };
            var second = new MediaItem("Second wallpaper") { Assets = [
                new() { Type = AssetType.Wallpaper, RelativePath = "Library/second.png" },
                new() { Type = AssetType.Cover, RelativePath = "Library/second.png" }] };
            using var fixture = new BigModeNavigationFixture([new("Root") { Items = [first, video, second] }]);
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            var artwork = view.FindControl<Grid>("PreviewArtwork")!;
            var wallpaper = view.FindControl<CrossfadeImage>("MediaWallpaper")!;
            var images = artwork.Children.OfType<CrossfadeImage>().ToArray();
            foreach (var image in images) image.FadeDurationMs = 0;
            try
            {
                WaitForImage();
                Assert.Contains(wallpaper.Children.OfType<Image>(), image => image.IsVisible && image.Opacity == 1);
                fixture.Session.TrySelect(1);
                Assert.True(fixture.Vm.MainVideoHasContent);
                Assert.False(artwork.IsVisible);
                AssertCleared();
                fixture.Vm.IsVideoOverlayVisible = true;
                fixture.Session.TrySelect(2);
                Assert.False(fixture.Vm.MainVideoHasContent);
                Assert.False(artwork.IsVisible); // The outgoing video has not finished fading.
                AssertCleared();

                // Make the still layer visible before its 220 ms load debounce.
                // Previously this exposed the video's wallpaper (or an older still).
                fixture.Vm.IsVideoOverlayVisible = false;
                Assert.True(artwork.IsVisible);
                Assert.Equal(directory.GetPath("Library/second.png"), wallpaper.Url);
                Assert.False(wallpaper.IsCurrentImageReady);
                Assert.All(images.SelectMany(image => image.Children.OfType<Image>()), image => Assert.False(image.IsVisible));

                // Also cover cancelling that load by navigating through a video
                // whose first frame has not appeared yet (overlay still false).
                fixture.Session.TrySelect(1);
                AssertCleared();
                fixture.Session.TrySelect(2);
                Assert.True(artwork.IsVisible);
                Assert.All(images.SelectMany(image => image.Children.OfType<Image>()), image => Assert.False(image.IsVisible));
                WaitForImage();
                Assert.Equal(directory.GetPath("Library/second.png"), wallpaper.Url);
                Assert.Contains(wallpaper.Children.OfType<Image>(), image =>
                    AsyncImageHelper.GetUrl(image) == wallpaper.Url && AsyncImageHelper.GetIsLoaded(image) && image.IsVisible);

                void AssertCleared()
                {
                    Assert.All(images, image => Assert.Null(image.Url));
                    Assert.All(images.SelectMany(image => image.Children.OfType<Image>()), image =>
                    {
                        Assert.False(image.IsVisible);
                        Assert.False(AsyncImageHelper.GetIsLoaded(image));
                    });
                }

                void WaitForImage()
                {
                    var timeout = Stopwatch.StartNew();
                    while (!wallpaper.IsCurrentImageReady)
                    {
                        if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Wallpaper did not become ready.");
                        dispatcher.RunJobs();
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                view.DataContext = null;
                foreach (var name in new[] { "first", "video", "second" }) AsyncImageHelper.InvalidateCache(directory.GetPath($"Library/{name}.png"));
            }
        });
    }

    [Fact]
    public async Task VideoSlot_AttachesOnlyTheSharedHostSurfacesAboveArtwork() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = new BigModeNavigationFixture([new("Root")]);
            var view = Parse();
            ManagedCursorFactory.EnsureRegistered();
            ThemeProperties.SetVideoFadeDurationMs(view, 0);
            var host = new BigModeHostView { DataContext = fixture.Vm };
            try
            {
                host.SetThemeContent(view, new Theme(view, new ThemeSounds(), AppContext.BaseDirectory,
                    primaryVideoEnabled: true, secondaryVideoEnabled: false,
                    navigationPresentation: NavigationPresentation.MixedEntries));
                var slot = view.FindControl<Border>("VideoSlot")!;
                var preview = Assert.IsType<CrossfadeVideoSurfaceControl>(slot.Child);
                Assert.Same(fixture.Vm.MainVideoSurfaceA, preview.SurfaceA);
                Assert.Same(fixture.Vm.MainVideoSurfaceB, preview.SurfaceB);
                Assert.Equal(Stretch.Uniform, preview.Stretch);
                fixture.Vm.IsVideoOverlayVisible = true;
                fixture.Vm.MainVideoActiveSurfaceIndex = 0;
                var area = view.FindControl<Grid>("PreviewArea")!;
                var artwork = view.FindControl<Grid>("PreviewArtwork")!;
                Assert.False(artwork.IsVisible);
                Assert.All(artwork.Children, child => Assert.False(child.IsEffectivelyVisible));
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindControl<Grid>("NavigatorStage")!.Background).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(area.Background).Color);
                area.Measure(new Size(1526, 780));
                area.Arrange(new Rect(0, 0, 1526, 780));
                Assert.Equal(area.Bounds.Size, slot.Bounds.Size);
                Assert.Equal(slot.Bounds.Size, preview.Bounds.Size);
                Assert.True(preview.IsVisible);
                Assert.All(preview.Children.OfType<VideoSurfaceControl>(), surface => Assert.Equal(Stretch.Uniform, surface.Stretch));
                Assert.Equal(1, slot.ZIndex);
                Assert.All(area.Children.Where(child => !ReferenceEquals(child, slot)), child => Assert.Equal(0, child.ZIndex));
                fixture.Vm.IsVideoOverlayVisible = false;
                Assert.False(preview.IsVisible);
                Assert.True(artwork.IsVisible);
                Assert.True(view.FindControl<CrossfadeImage>("CoverFallback")!.IsVisible);
                Assert.Same(preview, slot.Child);
            }
            finally { host.DataContext = null; view.DataContext = null; }
        });

    [Fact]
    public void Markup_UsesExistingPublicDataBoundedImagesAndSharedAchievementStates()
    {
        var root = XDocument.Load(ThemeFile).Root!;
        var elements = root.DescendantsAndSelf().ToArray();
        Assert.Single(elements, element => element.Name.LocalName == "ListBox");
        Assert.DoesNotContain(elements.Attributes(), attribute => attribute.Value.Contains("Rating", StringComparison.Ordinal));
        Assert.DoesNotContain(elements, element => element.Name.LocalName.Contains("VideoSurface", StringComparison.Ordinal));
        foreach (var field in new[] { "IsVisible", "IsLoading", "HasProgress", "Title", "SummaryText", "HardcoreSummaryText", "ShowStatus", "StatusText" })
            Assert.Contains(elements.Attributes(), attribute => attribute.Value.Contains("RetroAchievementsProgress." + field, StringComparison.Ordinal));
        foreach (var image in elements.Where(element => element.Name.LocalName is "Image" or "CrossfadeImage"))
        {
            var decode = Assert.Single(image.Attributes(), attribute => attribute.Name.LocalName.EndsWith("DecodeWidth", StringComparison.Ordinal));
            Assert.InRange(int.Parse(decode.Value), 1, 1600);
            Assert.DoesNotContain(image.Attributes(), attribute => attribute.Name.LocalName.EndsWith("DisableCache", StringComparison.Ordinal));
        }
        Visit(root, typeof(BigModeViewModel));

        static void Visit(XElement element, Type context)
        {
            if (element.Name.LocalName == "DataTemplate")
            {
                var type = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName is "DataType")?.Value;
                context = type switch
                {
                    "nav:BigModeNodeNavigationEntry" => typeof(BigModeNodeNavigationEntry),
                    "nav:BigModeMediaNavigationEntry" => typeof(BigModeMediaNavigationEntry),
                    _ => typeof(BigModeNavigationEntry)
                };
            }
            foreach (var attribute in element.Attributes())
            {
                var match = Regex.Match(attribute.Value, @"^\{(?:Reflection)?Binding ([^,}]+)");
                var bindingPath = element.Name.LocalName == "Binding" && attribute.Name.LocalName == "Path"
                    ? attribute.Value : match.Success ? match.Groups[1].Value : null;
                if (bindingPath == null) continue;
                var path = bindingPath.TrimStart('!');
                var type = (string?)element.Attribute("ElementName") == "PreviewArtwork" ? typeof(Grid) : context;
                const string parent = "$parent[UserControl].DataContext.";
                if (path.StartsWith(parent, StringComparison.Ordinal)) { path = path[parent.Length..]; type = typeof(BigModeViewModel); }
                if (path == "$parent[ListBoxItem].IsSelected") { path = "IsSelected"; type = typeof(ListBoxItem); }
                foreach (var part in path.Split('.'))
                {
                    var property = type.GetProperty(part, BindingFlags.Instance | BindingFlags.Public);
                    Assert.True(property != null, $"Unknown binding {attribute.Value} on {type.Name}");
                    type = property!.PropertyType;
                }
            }
            foreach (var child in element.Elements()) Visit(child, context);
        }
    }

    private static void Layout(Control view, Size size)
    {
        var viewport = view.FindControl<Viewbox>("NavigatorViewport")!;
        foreach (var control in viewport.GetVisualDescendants().OfType<Control>()
                     .Concat(view.GetLogicalDescendants().OfType<Control>()).Append(viewport).Distinct())
        {
            control.InvalidateMeasure();
            control.InvalidateArrange();
        }
        // The managed fixture has no application-level UserControl template.
        // Measure the actual scene directly so the assertions exercise real bounds.
        viewport.Measure(size);
        viewport.Arrange(new Rect(size));
    }

    private static void EnsureLayoutServices()
    {
        // Use the real asset loader and HarfBuzz text shaper without a windowing
        // backend. Platform registration is private in Avalonia 12 (as in
        // ManagedCursorFactory); no additional headless dependency is needed.
        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Register(typeof(IAssetLoader), new StandardAssetLoader());
        var shaper = Assert.Single(Assembly.Load("Avalonia.HarfBuzz").GetTypes(),
            type => !type.IsInterface && typeof(ITextShaperImpl).IsAssignableFrom(type));
        Register(typeof(ITextShaperImpl), Activator.CreateInstance(shaper, nonPublic: true)!);

        void Register(Type service, object instance)
        {
            var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(service).Invoke(locator, null)!;
            var register = binding.GetType().GetMethod("ToConstant")!;
            if (register.IsGenericMethodDefinition) register = register.MakeGenericMethod(instance.GetType());
            register.Invoke(binding, [instance]);
        }
    }

    private static Rect[] Bounds(Control view) =>
        new[] { "PreviewArea", "TitleArea", "MetadataArea", "DescriptionViewport" }
            .Select(name => view.FindControl<Control>(name)!.Bounds).ToArray();

    private sealed class Progress : IRetroAchievementsProgressService
    {
        public Task<RetroAchievementsProgressSnapshot> GetProgressAsync(int gameId, bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(new RetroAchievementsProgressSnapshot(
                new RetroAchievementsGameProgress { GameId = gameId, AchievementCount = 10, AwardedCount = 4, AwardedHardcoreCount = 2 },
                DateTimeOffset.UtcNow, false));
    }

    private sealed class Badges : IRetroAchievementsBadgeService
    {
        public Task<string?> GetBadgePathAsync(string? badgeName, bool isUnlocked,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
