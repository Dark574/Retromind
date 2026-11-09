using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Helpers;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Views;

/// <summary>
/// Coordinates root-theme replacement on the UI thread. The old presentation is
/// gated synchronously, before the VM publishes the new location's selection.
/// </summary>
internal sealed class BigModeThemeSwitcher : IDisposable
{
    private readonly BigModeViewModel _viewModel;
    private readonly BigModeHostView _host;
    private readonly Func<MediaNode?, string> _resolveThemePath;
    private readonly Func<string, Theme> _loadTheme;
    private readonly Func<Task> _prepareSwap;
    private string _appliedPath;
    private string _requestedPath;
    private int _generation;
    private bool _disposed;
    private Task _pendingSwap = Task.CompletedTask;

    public BigModeThemeSwitcher(BigModeViewModel viewModel, BigModeHostView host,
        string initialThemePath, Func<MediaNode?, string> resolveThemePath,
        Func<string, Theme> loadTheme, Func<Task> prepareSwap)
    {
        _viewModel = viewModel;
        _host = host;
        _appliedPath = _requestedPath = initialThemePath;
        _resolveThemePath = resolveThemePath;
        _loadTheme = loadTheme;
        _prepareSwap = prepareSwap;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void Refresh() => _ = RefreshSafelyAsync();

    internal Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        var path = _resolveThemePath(_viewModel.ThemeContextNode);
        if (path == _requestedPath) return _pendingSwap;
        _requestedPath = path;
        var generation = ++_generation;
        _host.BeginThemeSwap();
        return _pendingSwap = ApplyAsync(path, generation);
    }

    private async Task ApplyAsync(string path, int generation)
    {
        try
        {
            // Preserve VLC's stop/detach boundary, including its render pass.
            await _prepareSwap();
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!IsCurrent(generation)) return;
                var theme = _loadTheme(path);
                _viewModel.UpdateTheme(theme);
                _host.SetThemeContent(theme.View, theme);
                _appliedPath = path;
                _host.Focus();
            });
        }
        catch
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (IsCurrent(generation)) _requestedPath = _appliedPath;
            });
            throw;
        }
        finally
        {
            // An older request must neither install its view nor reveal the
            // outgoing tree while a newer request is still stopping playback.
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (IsCurrent(generation)) _host.CompleteThemeSwap();
            });
        }
    }

    private bool IsCurrent(int generation) => !_disposed && generation == _generation;

    private async Task RefreshSafelyAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[BigModeHost] Root theme switch failed: {ex}"); }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(BigModeViewModel.ThemeContextNode)) Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
