using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Retromind.Views;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeThemeSwitcherTests
{
    [Fact]
    public async Task OpeningDefaultNode_GatesSystemThemeBeforeIncomingMediaNotifications()
    {
        BigModeNavigationFixture? fixture = null;
        BigModeThemeSwitcher? switcher = null;
        BigModeHostView? host = null;
        Theme? oldTheme = null;
        var prepare = NewCompletion();
        Task? swap = null;
        var projectionsWhileGated = 0;
        var loaded = new List<string>();
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var node = new MediaNode("PlayStation") { ThemePath = "Default/theme.axaml", Items = [new("First"), new("Second")] };
            fixture = new BigModeNavigationFixture([node], NavigationPresentation.NodeBrowser);
            host = CreateHost(fixture.Vm);
            oldTheme = Load("System/theme.axaml");
            host.SetThemeContent(oldTheme.View, oldTheme);
            var systemSlot = oldTheme.View.FindControl<ContentControl>("SystemLayoutHost")!;
            var outgoingPreview = systemSlot.Content;
            switcher = new BigModeThemeSwitcher(fixture.Vm, host, "System/theme.axaml", Resolve,
                path => { loaded.Add(path); return Load(path); }, () => prepare.Task);
            fixture.Vm.PropertyChanged += (_, args) =>
            {
                if (!prepare.Task.IsCompleted && args.PropertyName is
                    nameof(BigModeViewModel.Items) or nameof(BigModeViewModel.SelectedItem) or nameof(BigModeViewModel.SelectedEntry))
                {
                    projectionsWhileGated++;
                    Assert.Equal(0, Presentation(host).Opacity);
                    Assert.False(Presentation(host).IsHitTestVisible);
                    Assert.True(host.FindControl<Border>("ThemeSwapBackdrop")!.IsVisible);
                }
            };

            fixture.Session.ActivateSelected();
            swap = switcher.RefreshAsync(); // Same request: await the production event handler's swap.
            Assert.Equal("First", fixture.Vm.SelectedItem!.Title);
            Assert.Same(outgoingPreview, systemSlot.Content); // No media view gets built in the outgoing System theme.
            Assert.IsNotType<BigModeMediaPreviewView>(systemSlot.Content);
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.Same(outgoingPreview, systemSlot.Content);
            Assert.Empty(loaded);
            Assert.True(projectionsWhileGated >= 3);
        });
        // A render pass while VLC is settling must not expose the old live tree.
        await UiThreadHelper.YieldToDispatcherAsync(DispatcherPriority.Render);
        await AvaloniaTestDispatcher.InvokeAsync(_ => Assert.Equal(0, Presentation(host!).Opacity));
        prepare.SetResult();
        await swap!.WaitAsync(TimeSpan.FromSeconds(5));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Equal(["Default/theme.axaml"], loaded);
            Assert.NotSame(oldTheme!.View, Presenter(host!).Content);
            Assert.Equal(1, Presentation(host!).Opacity);
            Assert.True(Presentation(host!).IsHitTestVisible);
            Assert.False(host!.FindControl<Border>("ThemeSwapBackdrop")!.IsVisible);
            Assert.Equal("Second", fixture!.Vm.SelectedItem!.Title);
            var currentView = Presenter(host!).Content;
            fixture.Vm.SelectPreviousCommand.Execute(null);
            Assert.Same(currentView, Presenter(host!).Content);
            Assert.Single(loaded);
            switcher!.Dispose();
            host!.DataContext = null;
            fixture.Dispose();
        });
    }

    [Fact]
    public async Task RapidOpenBackOpen_OnlyLatestSwapCanInstallOrRevealItsTheme()
    {
        BigModeNavigationFixture? fixture = null;
        BigModeThemeSwitcher? switcher = null;
        BigModeHostView? host = null;
        var prepares = new[] { NewCompletion(), NewCompletion(), NewCompletion() };
        var prepareCount = 0;
        var loaded = new List<string>();
        Task? first = null, back = null, latest = null;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var a = new MediaNode("Arcade") { ThemePath = "Arcade/theme.axaml", Items = [new("A")] };
            var b = new MediaNode("Default") { ThemePath = "Default/theme.axaml", Items = [new("B")] };
            fixture = new BigModeNavigationFixture([a, b], NavigationPresentation.NodeBrowser);
            host = CreateHost(fixture.Vm);
            var old = Load("System/theme.axaml");
            host.SetThemeContent(old.View, old);
            switcher = new BigModeThemeSwitcher(fixture.Vm, host, "System/theme.axaml", Resolve,
                path => { loaded.Add(path); return Load(path); }, () => prepares[prepareCount++].Task);
            fixture.Session.ActivateSelected();
            first = switcher.RefreshAsync();
            fixture.Session.Back();
            back = switcher.RefreshAsync();
            fixture.Session.TrySelect(1);
            fixture.Session.ActivateSelected();
            latest = switcher.RefreshAsync();
            Assert.Equal(3, prepareCount);
        });
        prepares[0].SetResult();
        await first!.WaitAsync(TimeSpan.FromSeconds(5));
        prepares[1].SetResult();
        await back!.WaitAsync(TimeSpan.FromSeconds(5));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Empty(loaded);
            Assert.Equal(0, Presentation(host!).Opacity);
        });
        prepares[2].SetResult();
        await latest!.WaitAsync(TimeSpan.FromSeconds(5));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Equal(["Default/theme.axaml"], loaded);
            Assert.Equal("B", fixture!.Vm.SelectedItem!.Title);
            Assert.Equal(1, Presentation(host!).Opacity);
            switcher!.Dispose();
            host!.DataContext = null;
            fixture.Dispose();
        });
    }

    [Fact]
    public async Task ClosingDuringSwap_DiscardsPendingLoadAndDoesNotRevealOutgoingTheme()
    {
        BigModeNavigationFixture? fixture = null;
        BigModeThemeSwitcher? switcher = null;
        BigModeHostView? host = null;
        Theme? old = null;
        var prepare = NewCompletion();
        var loads = 0;
        Task? swap = null;
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            fixture = new BigModeNavigationFixture([new MediaNode("Node") { Items = [new("Media")] }]);
            host = CreateHost(fixture.Vm);
            old = Load("System/theme.axaml");
            host.SetThemeContent(old.View, old);
            switcher = new BigModeThemeSwitcher(fixture.Vm, host, "System/theme.axaml", Resolve,
                path => { loads++; return Load(path); }, () => prepare.Task);
            fixture.Session.ActivateSelected();
            swap = switcher.RefreshAsync();
            switcher.Dispose();
        });
        prepare.SetResult();
        await swap!.WaitAsync(TimeSpan.FromSeconds(5));
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            Assert.Equal(0, loads);
            Assert.Same(old!.View, Presenter(host!).Content);
            Assert.Equal(0, Presentation(host!).Opacity);
            host!.DataContext = null;
            fixture!.Dispose();
        });
    }

    [Fact]
    public async Task SameThemeAcrossLocations_KeepsPresentationVisibleAndSkipsNativePreparation() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = new BigModeNavigationFixture([
                new MediaNode("A") { Items = [new("First")] }, new MediaNode("B") { Items = [new("Second")] }]);
            var host = CreateHost(fixture.Vm);
            var theme = Load("Default/theme.axaml");
            host.SetThemeContent(theme.View, theme);
            using var switcher = new BigModeThemeSwitcher(fixture.Vm, host, "Default/theme.axaml",
                node => node?.ThemePath ?? "Default/theme.axaml",
                path => throw new InvalidOperationException("No reload for the same theme"),
                () => throw new InvalidOperationException("No playback interruption for the same theme"));
            fixture.Session.ActivateSelected();
            fixture.Session.Back();
            fixture.Session.TrySelect(1);
            fixture.Session.ActivateSelected();
            Assert.Same(theme.View, Presenter(host).Content);
            Assert.Equal(1, Presentation(host).Opacity);
            Assert.Equal("Second", fixture.Vm.SelectedItem!.Title);
            Assert.True(switcher.RefreshAsync().IsCompletedSuccessfully);
            host.DataContext = null;
        });

    private static string Resolve(MediaNode? node) => node?.ThemePath ?? "System/theme.axaml";
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Grid Presentation(BigModeHostView host) => host.FindControl<Grid>("LibraryPresentation")!;
    private static ContentPresenter Presenter(BigModeHostView host) => host.FindControl<ContentPresenter>("ThemePresenter")!;
    private static BigModeHostView CreateHost(BigModeViewModel vm)
    {
        ManagedCursorFactory.EnsureRegistered();
        return new BigModeHostView { DataContext = vm };
    }
    private static Theme Load(string path)
    {
        var root = AvaloniaRuntimeXamlLoader.Parse<Control>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", path)));
        // These tests cover presentation ownership/order on the managed dispatcher.
        // Visual animation playback needs the application's real render clock.
        ThemeProperties.SetPrimaryVisualEnterMode(root, "None");
        ThemeProperties.SetSecondaryVisualEnterMode(root, "None");
        ThemeProperties.SetBackgroundVisualEnterMode(root, "None");
        return new Theme(root, new ThemeSounds(), AppContext.BaseDirectory, primaryVideoEnabled: false,
            secondaryVideoEnabled: false, navigationPresentation: ThemeProperties.GetNavigationPresentation(root));
    }
}
