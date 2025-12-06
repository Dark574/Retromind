using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // --- Import Actions ---

    private async void ImportRomsAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;
        if (SelectedNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) targetNode = SelectedNode;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.CtxImportRoms, 
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var sourcePath = folders[0].Path.LocalPath;

        var defaultExt = "iso,bin,cue,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64,exe,sh";
        var extensionsStr = await PromptForName(owner, "File extensions (comma separated):") ?? defaultExt;
        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var importedItems = await _importService.ImportFromFolderAsync(sourcePath, extensions);

        if (importedItems.Any())
        {
            foreach (var item in importedItems)
            {
                if (!targetNode.Items.Any(i => i.FilePath == item.FilePath))
                {
                    var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                
                    // Auto-assign existing assets
                    var existingCover = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover);
                    if (existingCover != null) item.CoverPath = existingCover;
                    var existingLogo = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Logo);
                    if (existingLogo != null) item.LogoPath = existingLogo;
                    targetNode.Items.Add(item);
                }
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }
    
    private async void ImportSteamAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportSteamGamesAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "No Steam games found.");
            return;
        }

        if (await ShowConfirmDialog(owner, $"Found {items.Count} Steam games. Import to '{targetNode.Name}'?"))
        {
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title)) targetNode.Items.Add(item);
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    private async void ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportHeroicGogAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "No Heroic/GOG installations found.");
            return;
        }

        if (await ShowConfirmDialog(owner, $"Found {items.Count} GOG games. Import?"))
        {
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title)) targetNode.Items.Add(item);
            }

            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    // --- Media & Scraping Actions ---

    private async void AddMediaAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.CtxAddMedia, AllowMultiple = true });

        if (result != null && result.Count > 0)
        {
            foreach (var file in result)
            {
                var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
                var title = await PromptForName(owner, $"{Strings.Title} '{file.Name}':") ?? rawTitle;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                var newItem = new MediaItem { Title = title, FilePath = file.Path.LocalPath, MediaType = MediaType.Native };
                targetNode.Items.Add(newItem); 
                var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                var existingCover = _fileService.FindExistingAsset(newItem, nodePath, MediaFileType.Cover);
                if (existingCover != null) newItem.CoverPath = existingCover;
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    private async void EditMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        var inherited = FindInheritedEmulator(item);
        var editVm = new EditMediaViewModel(item, _currentSettings, inherited) { StorageProvider = StorageProvider ?? owner.StorageProvider };
        var dialog = new EditMediaView { DataContext = editVm };
        editVm.RequestClose += saved => { dialog.Close(saved); };
        if (await dialog.ShowDialog<bool>(owner)) await SaveData();
    }

    private async void SetMusicAsync(MediaItem? item) 
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SelectMusic,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" } } }
        });

        if (result != null && result.Count == 1)
        {
            _audioService.StopMusic();
            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            var relativePath = _fileService.ImportAsset(sourceFile, item, nodePath, MediaFileType.Music);

            if (!string.IsNullOrEmpty(relativePath))
            {
                item.MusicPath = null;
                item.MusicPath = relativePath;
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                _audioService.PlayMusic(fullPath);
                await SaveData();
            }
        }
    }

    private async void ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return; 
    
        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        vm.OnResultSelected += async (result) => 
        {
            // Simple Conflict Resolution
            bool updateDesc = true;
            if (!string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description) && item.Description != result.Description)
            {
                var preview = item.Description.Length > 30 ? item.Description.Substring(0, 30) + "..." : item.Description;
                updateDesc = await ShowConfirmDialog(owner, $"Update Description? Current: '{preview}'");
            }
            else if (string.IsNullOrWhiteSpace(result.Description)) updateDesc = false;

            if (updateDesc) item.Description = result.Description;

            bool updateDev = true;
            if (!string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer) && !string.Equals(item.Developer, result.Developer, StringComparison.OrdinalIgnoreCase))
            {
                updateDev = await ShowConfirmDialog(owner, $"Update Developer? Old: {item.Developer}, New: {result.Developer}");
            }
            else if (string.IsNullOrWhiteSpace(result.Developer)) updateDev = false;

            if (updateDev) item.Developer = result.Developer;

            if (item.ReleaseDate.HasValue && result.ReleaseDate.HasValue && item.ReleaseDate.Value.Date != result.ReleaseDate.Value.Date)
            {
                if (await ShowConfirmDialog(owner, $"Update Date? Old: {item.ReleaseDate.Value:d}, New: {result.ReleaseDate.Value:d}"))
                    item.ReleaseDate = result.ReleaseDate;
            }
            else if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue) item.ReleaseDate = result.ReleaseDate;

            if (result.Rating.HasValue) item.Rating = result.Rating.Value;
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre)) item.Genre = result.Genre;
    
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            if (!string.IsNullOrEmpty(result.CoverUrl)) await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, true);
            if (!string.IsNullOrEmpty(result.WallpaperUrl)) await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, true);

            await SaveData();
            if (owner.OwnedWindows.FirstOrDefault(w => w.DataContext == vm) is Window dlg) dlg.Close();
        };

        var dialog = new ScrapeDialogView { DataContext = vm };
        await dialog.ShowDialog(owner);
    }

    private async void ScrapeNodeAsync(MediaNode? node)
    {
        if (SelectedNode != null && node != null && node.Id == SelectedNode.Id && node != SelectedNode) node = SelectedNode;
        if (node == null) node = SelectedNode;
        if (node == null || CurrentWindow is not { } owner) return;

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService);
        vm.OnItemScraped = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
    
            // Bulk Strategy: Only fill missing data (Safe Mode)
            if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description)) item.Description = result.Description;
            if (string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer)) item.Developer = result.Developer;
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre)) item.Genre = result.Genre;
            if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue) item.ReleaseDate = result.ReleaseDate;
            if (item.Rating == 0 && result.Rating.HasValue) item.Rating = result.Rating.Value;
    
            if (!string.IsNullOrEmpty(result.CoverUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.CoverPath);
                await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, shouldActivate);
            }
            if (!string.IsNullOrEmpty(result.WallpaperUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.WallpaperPath);
                await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, shouldActivate);
            }
        };
    
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private async Task DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, MediaFileType type, bool setAsActive)
    {
        try
        {
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;
            if (await AsyncImageHelper.SaveCachedImageAsync(url, tempPathWithExt)) success = true;
            else
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("User-Agent", "Retromind/1.0");
                    var data = await client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempPathWithExt, data);
                    success = true;
                }
                catch (Exception ex) { Debug.WriteLine($"Download Failed: {ex.Message}"); }
            }

            if (success)
            {
                var relativePath = _fileService.ImportAsset(tempPathWithExt, item, nodePath, type);
                if (setAsActive && !string.IsNullOrEmpty(relativePath))
                {
                    if (type == MediaFileType.Cover) item.CoverPath = relativePath;
                    if (type == MediaFileType.Wallpaper) item.WallpaperPath = relativePath;
                    if (type == MediaFileType.Logo) item.LogoPath = relativePath;
                }
            }
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
        }
        catch (Exception ex) { Debug.WriteLine($"Critical Download Error: {ex.Message}"); }
    }

    private void OpenIntegratedSearch()
    {
        _selectedNode = null;
        OnPropertyChanged(nameof(SelectedNode));
        var searchVm = new SearchAreaViewModel(RootItems) { ItemWidth = ItemWidth };
        searchVm.RequestPlay += item => { PlayMedia(item); };
        searchVm.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem))
            {
                 var item = searchVm.SelectedMediaItem;
                 if (item != null && !string.IsNullOrEmpty(item.MusicPath))
                     _audioService.PlayMusic(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath));
                 else _audioService.StopMusic();
            }
        };
        SelectedNodeContent = searchVm;
    }

    // Wrappers for Assets
    private async void SetCoverAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectCover, MediaFileType.Cover, (i, p) => i.CoverPath = p);
    private async void SetLogoAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectLogo, MediaFileType.Logo, (i, p) => i.LogoPath = p);
    private async void SetWallpaperAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectWallpaper, MediaFileType.Wallpaper, (i, p) => i.WallpaperPath = p);

    private EmulatorConfig? FindInheritedEmulator(MediaItem item)
    {
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode == null) return null;
        var nodeChain = GetNodeChain(parentNode, RootItems);
        nodeChain.Reverse();
        foreach (var node in nodeChain)
            if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                return _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
        return null;
    }

    private async Task RescanAllAssetsAsync()
    {
        await Task.Run(() => { foreach (var rootNode in RootItems) RescanNodeRecursive(rootNode); });
    }

    private void RescanNodeRecursive(MediaNode node)
    {
        var nodePath = PathHelper.GetNodePath(node, RootItems);
        foreach (var item in node.Items)
        {
            if (string.IsNullOrEmpty(item.CoverPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover); if (f != null) item.CoverPath = f; }
            if (string.IsNullOrEmpty(item.LogoPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Logo); if (f != null) item.LogoPath = f; }
            if (string.IsNullOrEmpty(item.WallpaperPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Wallpaper); if (f != null) item.WallpaperPath = f; }
            if (string.IsNullOrEmpty(item.MusicPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Music); if (f != null) item.MusicPath = f; }
        }
        foreach (var child in node.Children) RescanNodeRecursive(child);
    }
    
    private async Task SetAssetAsync(MediaItem? item, string title, MediaFileType type, Action<MediaItem, string> updateAction)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;
        var result = await (StorageProvider ?? owner.StorageProvider).OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
        if (result != null && result.Count == 1)
        {
            var relPath = _fileService.ImportAsset(result[0].Path.LocalPath, item, PathHelper.GetNodePath(SelectedNode, RootItems), type);
            if (!string.IsNullOrEmpty(relPath)) { updateAction(item, null!); updateAction(item, relPath); await SaveData(); }
        }
    }
}