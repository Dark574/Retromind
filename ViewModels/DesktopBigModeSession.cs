using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

internal sealed class DesktopBigModeSession
{
    private readonly AppSettings _settings;
    private readonly Func<Window?> _getWindow;
    private readonly Func<MediaItem, Task<LaunchResult?>> _playMedia;
    private readonly Func<Task> _syncDesktopSelection;
    private readonly Action<BigModeHostView> _restoreGameFocus;
    private Presentation? _presentation;

    internal DesktopBigModeSession(
        AppSettings settings,
        Func<Window?> getWindow,
        Func<MediaItem, Task<LaunchResult?>> playMedia,
        Func<Task> syncDesktopSelection,
        Action<BigModeHostView>? restoreGameFocus = null)
    {
        _settings = settings;
        _getWindow = getWindow;
        _playMedia = playMedia;
        _syncDesktopSelection = syncDesktopSelection;
        _restoreGameFocus = restoreGameFocus ?? RestoreGameFocus;
    }

    internal BigModeHostView? Content => _presentation?.Host;
    internal bool IsOpen => _presentation != null;
    internal BigModeViewModel? ActiveViewModel => _presentation is { IsClosing: false } open ? open.ViewModel : null;
    internal event Action? ContentChanged;

    internal void Open(Func<Theme, BigModeViewModel> createViewModel)
    {
        if (IsOpen)
            return;

        var window = _getWindow();
        var previousWindowState = window?.WindowState ?? WindowState.Maximized;
        if (window != null && previousWindowState != WindowState.FullScreen)
            window.WindowState = WindowState.FullScreen;

        // BigMode opens at the virtual root, with its own theme setting.
        var initialThemePath = GetEffectiveThemePath(null);
        var initialTheme = ThemeLoader.LoadTheme(initialThemePath);
        var viewModel = createViewModel(initialTheme);
        var host = new BigModeHostView
        {
            DataContext = viewModel,
            Focusable = true,
            Opacity = 0,
            IsHitTestVisible = false
        };
        var themeSwitcher = new BigModeThemeSwitcher(
            viewModel, host, initialThemePath, GetEffectiveThemePath, ThemeLoader.LoadTheme,
            viewModel.PrepareForThemeSwapAsync);
        var presentation = new Presentation(viewModel, host, previousWindowState, themeSwitcher);

        viewModel.RequestPlay += item => PlayAsync(presentation, item);
        viewModel.RequestApplicationExit += () => UiThreadHelper.InvokeAsync(() => _getWindow()?.Close());

        // Attach the host while hidden so fullscreen sizing and layout can settle before it is revealed.
        _presentation = presentation;
        ContentChanged?.Invoke();
        host.SetThemeContent(initialTheme.View, initialTheme);
        host.RevealAfterInitialLayout();

        themeSwitcher.Refresh();
        viewModel.RequestClose += () => UiThreadHelper.InvokeAsync(() => CloseOnUiThread(presentation, restoreDesktop: true));

        viewModel.InvalidatePreviewCaches(stopCurrentPreview: false);
    }

    internal Task ShutdownAsync() => UiThreadHelper.InvokeAsync(() =>
        _presentation is { } presentation
            ? CloseOnUiThread(presentation, restoreDesktop: false)
            : Task.CompletedTask);

    private Task CloseOnUiThread(Presentation presentation, bool restoreDesktop)
    {
        if (!ReferenceEquals(_presentation, presentation))
            return Task.CompletedTask;
        if (presentation.IsClosing)
            return presentation.CloseTask;

        presentation.IsClosing = true;
        return presentation.CloseTask = FinishCloseAsync(presentation, restoreDesktop);
    }

    private async Task FinishCloseAsync(Presentation presentation, bool restoreDesktop)
    {
        var viewModel = presentation.ViewModel;
        presentation.ThemeSwitcher.Dispose();

        try
        {
            // Keep the original host until VLC has stopped and released its
            // resources. The worker never needs the UI thread to finish.
            try
            {
                viewModel.SaveState();
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoreApp] BigMode teardown failed: {ex}");
        }

        _presentation = null;
        ContentChanged?.Invoke();
        if (!restoreDesktop)
            return;

        var window = _getWindow();
        if (window is { WindowState: WindowState.FullScreen } &&
            presentation.PreviousWindowState != WindowState.FullScreen)
            window.WindowState = presentation.PreviousWindowState;

        try
        {
            await _syncDesktopSelection();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoreApp] Could not restore desktop selection: {ex}");
        }
    }

    private async Task PlayAsync(Presentation presentation, MediaItem item)
    {
        var result = await _playMedia(item);
        if (result?.WasSessionTracked != true)
            return;

        // Only a tracked session has a reliable game-end boundary for restoring focus.
        await UiThreadHelper.InvokeAsync(() =>
        {
            if (ReferenceEquals(_presentation, presentation) && !presentation.IsClosing)
                _restoreGameFocus(presentation.Host);
        }, DispatcherPriority.Input);
    }

    private void RestoreGameFocus(BigModeHostView host)
    {
        if (_getWindow() is not { } window)
            return;

        if (window.WindowState != WindowState.FullScreen)
            window.WindowState = WindowState.FullScreen;

        window.Activate();
        host.Focus();
    }

    /// <summary>
    /// Resolves the effective theme file path for a navigation context. A null
    /// context represents the virtual library root and uses its dedicated setting.
    /// Every real node uses only its own ThemePath; themes are never inherited.
    /// Missing or invalid assignments fall back to Navigator, then the legacy Default theme.
    /// </summary>
    private string GetEffectiveThemePath(MediaNode? startNode) =>
        ResolveEffectiveThemePath(
            startNode,
            _settings.RootBigModeThemePath,
            AppPaths.ThemesRoot);

    internal static string ResolveEffectiveThemePath(
        MediaNode? node,
        string? rootThemePath,
        string themesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themesRoot);

        var assignedThemePath = node == null ? rootThemePath : node.ThemePath;
        if (ThemePathResolver.TryResolveThemeFilePath(
                themesRoot,
                assignedThemePath,
                out var assignedFullPath))
        {
            return assignedFullPath;
        }

        if (!string.IsNullOrWhiteSpace(assignedThemePath))
            Debug.WriteLine($"[Theme] Assigned theme path is invalid or unavailable: '{assignedThemePath}'.");

        if (!ThemePathResolver.TryResolveThemeFilePath(
                themesRoot,
                ThemeDiscovery.DefaultThemePath,
                out var fallbackThemePath) &&
            !ThemePathResolver.TryResolveThemeFilePath(
                themesRoot,
                ThemeDiscovery.LegacyDefaultThemePath,
                out fallbackThemePath))
        {
            fallbackThemePath = Path.GetFullPath(
                Path.Combine(themesRoot, ThemeDiscovery.DefaultThemePath));
        }
        Debug.WriteLine($"[Theme] No valid local theme assigned, using fallback: {fallbackThemePath}");
        return fallbackThemePath;
    }

    private sealed record Presentation(BigModeViewModel ViewModel, BigModeHostView Host,
        WindowState PreviousWindowState, BigModeThemeSwitcher ThemeSwitcher)
    {
        internal bool IsClosing { get; set; }
        internal Task CloseTask { get; set; } = Task.CompletedTask;
    }
}
