using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class NodeSettingsView : Window
{
    public NodeSettingsView()
    {
        InitializeComponent();
    }

    // <summary>
    /// Opens a file picker and imports a logo image as a node-level asset
    /// using the same convention as media assets.
    /// </summary>
    private async void OnBrowseLogoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        var file = await PickSingleFileAsync(new FilePickerOpenOptions
        {
            Title = "Select logo image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                },
                FilePickerFileTypes.All
            }
        });

        if (file is null)
            return;

        var localPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        var relative = await vm.ImportNodeAssetAsync(localPath, AssetType.Logo);
        if (!string.IsNullOrWhiteSpace(relative))
            vm.NodeLogoPath = relative;
    }

    /// <summary>
    /// Opens a file picker and imports a wallpaper image as a node-level asset.
    /// </summary>
    private async void OnBrowseWallpaperClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        var file = await PickSingleFileAsync(new FilePickerOpenOptions
        {
            Title = "Select wallpaper image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                },
                FilePickerFileTypes.All
            }
        });

        if (file is null)
            return;

        var localPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        var relative = await vm.ImportNodeAssetAsync(localPath, AssetType.Wallpaper);
        if (!string.IsNullOrWhiteSpace(relative))
            vm.NodeWallpaperPath = relative;
    }

    /// <summary>
    /// Opens a file picker and sets the NodeVideoPath on the view model.
    /// </summary>
    private async void OnBrowseVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        var file = await PickSingleFileAsync(new FilePickerOpenOptions
        {
            Title = "Select video file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.webm", "*.avi" }
                },
                FilePickerFileTypes.All
            }
        });

        if (file is null)
            return;

        var localPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        var relative = await vm.ImportNodeAssetAsync(localPath, AssetType.Video);
        if (!string.IsNullOrWhiteSpace(relative))
            vm.NodeVideoPath = relative;
    }

    // --- CLEAR HANDLERS ---

    /// <summary>
    /// Clears the logo path. The corresponding MediaAsset entry will be removed
    /// when the user saves the dialog (via UpdateNodeAsset in the view model).
    /// </summary>
    private void OnClearLogoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        vm.NodeLogoPath = null;
    }

    /// <summary>
    /// Clears the wallpaper path and removes the stored wallpaper asset on save.
    /// </summary>
    private void OnClearWallpaperClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        vm.NodeWallpaperPath = null;
    }

    /// <summary>
    /// Clears the video path and removes the stored video asset on save.
    /// </summary>
    private void OnClearVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeSettingsViewModel vm)
            return;

        vm.NodeVideoPath = null;
    }
    
    /// <summary>
    /// Helper that shows the system file picker and returns the first selected file (if any).
    /// Returns null if the storage provider is not available or the user cancels.
    /// </summary>
    private async Task<IStorageFile?> PickSingleFileAsync(FilePickerOpenOptions options)
    {
        var provider = StorageProvider;
        if (provider is null)
            return null;

        var result = await provider.OpenFilePickerAsync(options);
        return result?.FirstOrDefault();
    }
}