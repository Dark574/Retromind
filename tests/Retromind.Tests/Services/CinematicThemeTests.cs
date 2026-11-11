using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Retromind.Views;
using SkiaSharp;

namespace Retromind.Tests.Services;

public sealed class CinematicThemeTests
{
    private static string ThemeFile => Path.Combine(AppContext.BaseDirectory, "Themes", "Cinematic", "theme.axaml");
    private static Control Parse() => AvaloniaRuntimeXamlLoader.Parse<Control>(File.ReadAllText(ThemeFile));

    [Fact]
    public async Task DiscoveryAndLoader_ExposeCinematicWithSharedHostCapabilities()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateFile("Themes/Cinematic/theme.axaml", File.ReadAllText(ThemeFile));
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Contains("Cinematic/theme.axaml", ThemeDiscovery.GetAvailableThemePaths());
            var theme = ThemeLoader.LoadTheme("Cinematic/theme.axaml");
            Assert.Equal("Cinematic", theme.Name);
            Assert.Equal("0.1.2", theme.Version);
            Assert.Equal(NavigationPresentation.MixedEntries, theme.NavigationPresentation);
            Assert.True(theme.SupportsHome);
            Assert.True(theme.PrimaryVideoEnabled);
            Assert.False(theme.SecondaryVideoEnabled);
            Assert.True(theme.AttractModeEnabled);
            Assert.Equal(TimeSpan.FromSeconds(75), theme.AttractModeIdleInterval);
            Assert.NotNull(theme.View.FindControl<Border>(theme.VideoSlotName));
            Assert.NotSame(theme.View, theme.CreateView());
            Assert.True(ThemeProperties.GetProvidesSystemMenuHint(theme.View));
            Assert.Equal("Uniform", ThemeProperties.GetVideoStretchMode(theme.View));
            Assert.False(ThemeProperties.GetVideoRetainPreviousFrameDuringFade(theme.View));
        });
    }

    [Fact]
    public async Task MixedBindings_OpenLaunchFilterAndRestoreWithoutChangingDescriptions() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var description = string.Join('\n', Enumerable.Repeat("The complete original description.", 12));
            var child = new MediaNode("Empty category") { Description = "Category description" };
            var item = new MediaItem("A long media title") { Description = description };
            var hidden = new MediaItem("Protected") { IsProtected = true };
            var root = new MediaNode("Mixed") { Children = [child], Items = [item, hidden] };
            using var fixture = new BigModeNavigationFixture([root], parentalFilter: true);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var shelf = view.FindControl<ListBox>("CinematicShelf")!;
                var title = view.FindControl<TextBlock>("HeroTitle")!;
                var details = view.FindControl<TextBlock>("HeroDescription")!;
                var open = view.FindControl<Button>("OpenCategoryAction")!;
                var start = view.FindControl<Button>("StartMediaAction")!;
                Assert.Equal(2, shelf.ItemCount);
                Assert.Same(fixture.Vm.SelectedEntry, shelf.SelectedItem);
                Assert.Equal(child.Name, title.Text);
                Assert.Equal(child.Description, details.Text);
                Assert.True(open.IsVisible);
                Assert.False(start.IsVisible);
                Assert.Equal(fixture.Vm.HostNavigationActionText, open.Content);
                Assert.Equal(Strings.Button_Play, start.Content);

                MediaItem? launched = null;
                fixture.Vm.RequestPlay += selected => { launched = selected; return Task.CompletedTask; };
                open.Command!.Execute(null);
                Assert.Null(launched);
                Assert.Same(child, fixture.Vm.CurrentNode);
                Assert.Equal(0, shelf.ItemCount);
                Assert.True(fixture.Vm.IsNavigationEmpty);
                Assert.False(view.FindControl<Grid>("IdentityPanel")!.IsVisible);
                fixture.Vm.ExitBigModeCommand.Execute(null);
                Assert.Same(child, fixture.Vm.SelectedCategory);
                Assert.Same(fixture.Vm.SelectedEntry, shelf.SelectedItem);

                // Pointer/list selection and controller selection share the session cursor.
                shelf.SelectedItem = fixture.Vm.NavigationEntries[1];
                Assert.Same(item, fixture.Vm.SelectedItem);
                Assert.Equal(item.Title, title.Text);
                Assert.Equal(description, details.Text);
                Assert.Equal(3, details.MaxLines);
                Assert.Equal(description, item.Description);
                Assert.False(open.IsVisible);
                Assert.True(start.IsVisible);
                start.Command!.Execute(null);
                Assert.Same(item, launched);
                fixture.Vm.ExitBigModeCommand.Execute(null);
                fixture.Vm.PlayCurrentCommand.Execute(null);
                Assert.Same(item, fixture.Vm.SelectedItem);
                Assert.Same(fixture.Vm.SelectedEntry, shelf.SelectedItem);
            }
            finally { view.DataContext = null; }
        });

    [Fact]
    public async Task CardTemplates_DistinguishCategoriesAndPreserveCoverAspectRatios() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Mixed") { Children = [new("Child")], Items = [new("Media")] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            var shelf = view.FindControl<ListBox>("CinematicShelf")!;
            foreach (var entry in fixture.Vm.NavigationEntries)
            {
                var card = shelf.ItemTemplate!.Build(entry)!;
                var descendants = card.GetLogicalDescendants().ToArray();
                Assert.All(descendants.OfType<Image>(), image => Assert.Equal(Stretch.Uniform, image.Stretch));
                var badge = Assert.Single(descendants.OfType<ContentControl>());
                var template = Assert.Single(badge.DataTemplates, template => template.Match(entry));
                var content = template.Build(entry)!;
                if (entry is BigModeNodeNavigationEntry)
                    Assert.Single(content.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>());
                else
                    Assert.False(content.IsVisible);
            }
        });

    [Fact]
    public async Task LargeLevel_UsesFiniteScaledViewportAndRetainsEntriesDuringNavigation() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Large")
            {
                Children = [new("Child")],
                Items = new(Enumerable.Range(0, 10000).Select(index => new MediaItem($"Media {index}")))
            };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var viewport = view.FindControl<Viewbox>("CinematicViewport")!;
                var stage = view.FindControl<Grid>("CinematicStage")!;
                var shelf = view.FindControl<ListBox>("CinematicShelf")!;
                Assert.Equal(Stretch.Uniform, viewport.Stretch);
                Assert.Equal(2560, stage.Width);
                Assert.Equal(1440, stage.Height);
                var panel = Assert.IsType<VirtualizingStackPanel>(shelf.ItemsPanel!.Build());
                Assert.Equal(Avalonia.Layout.Orientation.Horizontal, panel.Orientation);
                Assert.Equal(0.5, panel.CacheLength);
                Assert.False(ThemeProperties.GetUseHostSelectionEffects(shelf));
                var entries = fixture.Vm.NavigationEntries;
                var source = shelf.ItemsSource;
                var version = fixture.Session.LibraryVersion;
                for (var index = 0; index < 100; index++) fixture.Vm.SelectNextCommand.Execute(null);
                Assert.Same(entries, fixture.Vm.NavigationEntries);
                Assert.Same(source, shelf.ItemsSource);
                Assert.Equal(version, fixture.Session.LibraryVersion);
                Assert.Equal(10001, shelf.ItemCount);
                Assert.Same(fixture.Vm.SelectedEntry, shelf.SelectedItem);
                Assert.Equal(100, fixture.Vm.SelectedEntryIndex);
            }
            finally { view.DataContext = null; }
        });

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    public async Task Gradients_UseRelativeCoordinatesAndDoNotObscureTheUpperHero(int width, int height) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var view = Parse();
            var size = new Size(width, height);
            // The original "0,1" endpoint is one pixel, not the bottom of the viewport.
            var oldEnd = RelativePoint.Parse("0,1");
            Assert.Equal(RelativeUnit.Absolute, oldEnd.Unit);
            Assert.Equal(new Point(0, 1), oldEnd.ToPixels(size));
            foreach (var key in new[] { "EmptyBackdrop", "IdentityShade", "HeroBottomShade" })
            {
                var brush = Assert.IsType<LinearGradientBrush>(view.Resources[key]);
                Assert.Equal(RelativeUnit.Relative, brush.StartPoint.Unit);
                Assert.Equal(RelativeUnit.Relative, brush.EndPoint.Unit);
                var delta = brush.EndPoint.ToPixels(size) - brush.StartPoint.ToPixels(size);
                Assert.True(Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) > 1000);
            }

            var shade = Assert.IsType<LinearGradientBrush>(view.FindControl<Avalonia.Controls.Shapes.Rectangle>("HeroShade")!.Fill);
            Assert.Equal(new Point(0, height), shade.EndPoint.ToPixels(size));
            Assert.Equal(0, shade.GradientStops[0].Color.A);
            Assert.Equal(0, shade.GradientStops.Single(stop => stop.Offset == 0.40).Color.A);
            Assert.All(shade.GradientStops, stop => Assert.True(stop.Color.A < 255));

            var scene = view.FindControl<Grid>("CinematicScene")!;
            var wallpaper = view.FindControl<Grid>("BackgroundVisual")!;
            var slot = view.FindControl<Border>("VideoSlot")!;
            var overlay = view.FindControl<Viewbox>("CinematicViewport")!;
            var stage = view.FindControl<Grid>("CinematicStage")!;
            Assert.Same(scene, wallpaper.Parent);
            Assert.Same(stage, slot.Parent);
            Assert.Same(scene, overlay.Parent);
            Assert.Same(slot, stage.Children[0]);
            Assert.True(wallpaper.ZIndex < view.FindControl<Control>("HeroShade")!.ZIndex);
            Assert.True(view.FindControl<Control>("HeroShade")!.ZIndex < overlay.ZIndex);
            Assert.Null(stage.Background);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WallpaperBindings_UseItemOrEnabledOwnerWallpaperAndSelectedNodesOwnArtwork(bool inherit)
    {
        using var directory = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", directory.GetPath("Retromind.AppImage")));
        directory.CreateDirectory("Library");
        using (var bitmap = new SKBitmap(16, 9))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            foreach (var name in new[] { "child", "owner", "item" })
                File.WriteAllBytes(directory.GetPath($"Library/{name}-wallpaper.png"), encoded.ToArray());
        }
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var child = new MediaNode("Child")
            {
                WallpaperFallbackEnabled = false,
                Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/child-wallpaper.png" }]
            };
            var item = new MediaItem("Wallpaper without screenshots");
            if (!inherit)
                item.Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/item-wallpaper.png" },
                    new() { Type = AssetType.Screenshot, RelativePath = "Library/missing-screenshot.png" }];
            var root = new MediaNode("Owner")
            {
                Children = [child], Items = [item], WallpaperFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Wallpaper, RelativePath = "Library/owner-wallpaper.png" }]
            };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var view = Parse();
            view.DataContext = fixture.Vm;
            try
            {
                var nodeWallpaper = view.FindControl<CrossfadeImage>("NodeWallpaper")!;
                var mediaWallpaper = view.FindControl<CrossfadeImage>("MediaWallpaper")!;
                Assert.Equal(directory.GetPath("Library/child-wallpaper.png"), nodeWallpaper.Url);
                Assert.True(nodeWallpaper.IsVisible);
                Assert.False(mediaWallpaper.IsVisible);
                fixture.Vm.SelectNextCommand.Execute(null);
                var expected = AssetResolver.ResolveAssetPath(item, root, AssetType.Wallpaper);
                Assert.Equal(directory.GetPath(inherit ? "Library/owner-wallpaper.png" : "Library/item-wallpaper.png"), expected);
                Assert.Equal(expected, mediaWallpaper.Url);
                Assert.True(mediaWallpaper.IsVisible);
                Assert.False(nodeWallpaper.IsVisible);
                Assert.Null(view.FindControl<CrossfadeImage>("ScreenshotFallback")!.Url);
                fixture.Vm.SelectPreviousCommand.Execute(null);
                Assert.Equal(child.PrimaryWallpaperAbsolutePath, nodeWallpaper.Url);
                Assert.Null(mediaWallpaper.Url);
            }
            finally { view.DataContext = null; }
        });
    }

    [Fact]
    public async Task VideoSlot_AttachesTheSharedSurfacesWithPositiveBoundsAndHostOwnedVisibility() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = new BigModeNavigationFixture([new("Root")]);
            var view = Parse();
            ThemeProperties.SetVideoFadeDurationMs(view, 0);
            ManagedCursorFactory.EnsureRegistered();
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
                Assert.Equal(950, slot.Width);
                Assert.Equal(535, slot.Height);
                Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, slot.HorizontalAlignment);
                Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, slot.VerticalAlignment);
                Assert.Equal(new Thickness(0, 96, 96, 0), slot.Margin);
                Assert.Null(slot.Background);
                Assert.Equal(default(BoxShadows), slot.BoxShadow);
                Assert.True(slot.IsVisible);

                fixture.Vm.IsVideoOverlayVisible = true;
                fixture.Vm.MainVideoActiveSurfaceIndex = 0;
                Assert.True(preview.IsVisible);
                Assert.Equal(0, preview.ActiveIndex);
                slot.Measure(new Size(2560, 1030));
                slot.Arrange(new Rect(0, 0, 2560, 1030));
                Assert.Equal(new Rect(1514, 96, 950, 535), slot.Bounds);
                Assert.Equal(new Size(950, 535), preview.Bounds.Size);
                Assert.All(preview.Children.OfType<VideoSurfaceControl>(), surface =>
                {
                    Assert.Equal(preview.Bounds.Size, surface.Bounds.Size);
                    Assert.Equal(Stretch.Uniform, surface.Stretch);
                });

                fixture.Vm.MainVideoActiveSurfaceIndex = 1;
                Assert.Equal(1, preview.ActiveIndex);
                Assert.Same(preview, slot.Child);
                fixture.Vm.IsVideoOverlayVisible = false;
                Assert.False(preview.IsVisible);
                Assert.True(slot.IsVisible);
                Assert.True(view.FindControl<Grid>("BackgroundVisual")!.IsVisible);
            }
            finally { host.DataContext = null; view.DataContext = null; }
        });

    [Fact]
    public void Axaml_UsesPublicBindingsExistingMetadataAndOneCanonicalList()
    {
        var root = XDocument.Load(ThemeFile).Root!;
        var elements = root.DescendantsAndSelf().ToArray();
        var list = Assert.Single(elements, element => element.Name.LocalName == "ListBox");
        Assert.Equal("{Binding NavigationEntries}", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedEntry, Mode=TwoWay}", (string?)list.Attribute("SelectedItem"));
        Assert.Equal("{Binding SelectedEntryIndex, Mode=OneWay}", (string?)list.Attribute("SelectedIndex"));
        Assert.DoesNotContain(elements, element => (string?)element.Attribute("Text") == "CINEMATIC");
        Assert.DoesNotContain(elements.Attributes(), attribute => attribute.Value.Contains("Rating", StringComparison.Ordinal));
        foreach (var attribute in elements.Attributes().Where(attribute => attribute.Name.LocalName.StartsWith("ThemeProperties.", StringComparison.Ordinal)))
        {
            var name = attribute.Name.LocalName.Split('.')[1];
            Assert.NotNull(typeof(ThemeProperties).GetMethod("Get" + name));
            Assert.NotNull(typeof(ThemeProperties).GetMethod("Set" + name));
        }
        foreach (var image in elements.Where(element => element.Name.LocalName is "Image" or "CrossfadeImage"))
        {
            Assert.DoesNotContain(image.Attributes(), attribute => attribute.Name.LocalName.EndsWith("DisableCache", StringComparison.Ordinal) && attribute.Value == "True");
            if (image.Name.LocalName == "CrossfadeImage")
                Assert.InRange(int.Parse(image.Attribute("DecodeWidth")!.Value), 1, 1920);
        }
        Visit(root, typeof(BigModeViewModel));

        void Visit(XElement element, Type context)
        {
            if (element.Name.LocalName == "DataTemplate") context = typeof(BigModeNavigationEntry);
            foreach (var attribute in element.Attributes())
            {
                var match = Regex.Match(attribute.Value, @"^\{(?:Reflection)?Binding ([^,}]+)");
                if (!match.Success) continue;
                var path = match.Groups[1].Value.TrimStart('!');
                var type = context;
                const string parentVm = "$parent[UserControl].DataContext.";
                if (path.StartsWith(parentVm, StringComparison.Ordinal))
                {
                    path = path[parentVm.Length..];
                    type = typeof(BigModeViewModel);
                }
                if (path == "$parent[ListBoxItem].IsSelected")
                {
                    path = "IsSelected";
                    type = typeof(ListBoxItem);
                }
                foreach (var part in path.Split('.'))
                {
                    var property = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                    Assert.True(property != null, $"Unknown binding {attribute.Value} on {type.Name}");
                    type = property!.PropertyType;
                }
            }
            foreach (var child in element.Elements()) Visit(child, context);
        }
    }
}
