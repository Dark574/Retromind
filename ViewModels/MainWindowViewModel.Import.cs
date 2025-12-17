using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // --- Import Actions ---

    private async Task ImportRomsAsync(MediaNode? targetNode)
    {
        // Resolve target node
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;
        
        // If the context menu was opened on a node that is ALSO the selected node, use the object reference to be safe
        if (SelectedNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) 
            targetNode = SelectedNode;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Ctx_ImportRoms, 
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var sourcePath = folders[0].Path.LocalPath;

        var defaultExt = "iso,bin,cue,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64,exe,sh";
        var extensionsStr = await PromptForName(owner, Strings.Dialog_FileExtensionsPrompt) ?? defaultExt;
        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        // Run heavy import logic
        var importedItems = await _importService.ImportFromFolderAsync(sourcePath, extensions);

        if (importedItems.Any())
        {
            var anyAdded = false;
            
            foreach (var item in importedItems)
            {
                // Duplicate Check based on FilePath
                if (!targetNode.Items.Any(i => i.FilePath == item.FilePath))
                {
                    targetNode.Items.Add(item);
                    anyAdded = true;

                    // Scan existing assets for newly imported items.
                    var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                    _fileService.RefreshItemAssets(item, nodePath);
                }
            }
            
            if (anyAdded)
            {
                MarkLibraryDirty();
                SortMediaItems(targetNode.Items);
                await SaveData();

                if (IsNodeInCurrentView(targetNode)) UpdateContent();
            }
        }
    }
    
    private async Task ImportSteamAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportSteamGamesAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, Strings.Dialog_NoSteamGamesFound);
            return;
        }

        var message = string.Format(Strings.Dialog_ConfirmImportSteamFormat, items.Count, targetNode.Name);
        if (await ShowConfirmDialog(owner, message))
        {
            var anyAdded = false;
            
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title))
                {
                    targetNode.Items.Add(item);
                    anyAdded = true;
                }
            }
            
            if (anyAdded)
            {
                MarkLibraryDirty();
                SortMediaItems(targetNode.Items);
                await SaveData();
                if (IsNodeInCurrentView(targetNode)) UpdateContent();
            }
        }
    }

    private async Task ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportHeroicGogAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, Strings.Dialog_NoGogInstallationsFound);
            return;
        }

        var message = string.Format(Strings.Dialog_ConfirmImportGogFormat, items.Count);
        if (await ShowConfirmDialog(owner, message))
        {
            var anyAdded = false;
            
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title))
                {
                    targetNode.Items.Add(item);
                    anyAdded = true;
                }
            }

            if (anyAdded)
            {
                MarkLibraryDirty();
                SortMediaItems(targetNode.Items);
                await SaveData();
                if (IsNodeInCurrentView(targetNode)) UpdateContent();
            }
        }
    }

    // --- Media & Scraping Actions ---

    private async Task AddMediaAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) 
            targetNode = SelectedNode;
            
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions 
        { 
            Title = Strings.Ctx_Media_Add, 
            AllowMultiple = true 
        });

        if (result != null && result.Count > 0)
        {
            var anyAdded = false;
            
            foreach (var file in result)
            {
                var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
                var title = await PromptForName(owner, $"{Strings.Common_Title} '{file.Name}':") ?? rawTitle;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                var newItem = new MediaItem { Title = title, FilePath = file.Path.LocalPath, MediaType = MediaType.Native };
                targetNode.Items.Add(newItem); 
                anyAdded = true;
                
                // Auch hier direkt scannen, falls Assets existieren
                var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                _fileService.RefreshItemAssets(newItem, nodePath);
            }
            
            if (anyAdded)
            {
                MarkLibraryDirty();
                SortMediaItems(targetNode.Items);
                await SaveData();
                if (IsNodeInCurrentView(targetNode)) UpdateContent();
            }
        }
    }

    private async Task EditMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        var inherited = FindInheritedEmulator(item);
        
        // Find parent node to build the correct path for the FileService (organization)
        var parentNode = FindParentNode(RootItems, item);
        var nodePath = parentNode != null 
            ? PathHelper.GetNodePath(parentNode, RootItems) 
            : new List<string>();

        // Inject FileService and NodePath
        var editVm = new EditMediaViewModel(item, _currentSettings, _fileService, nodePath, inherited) 
        { 
            StorageProvider = StorageProvider ?? owner.StorageProvider 
        };
        
        var dialog = new EditMediaView { DataContext = editVm };
        
        // bool? damit "X" (kein Ergebnis) eindeutig erkennbar bleibt
        var result = await dialog.ShowDialog<bool?>(owner);
        
        if (result == true)
        {
            MarkLibraryDirty();
            await SaveData();

            if (parentNode != null && IsNodeInCurrentView(parentNode))
                UpdateContent();
        }
    }

    private async Task SetMusicAsync(MediaItem? item) 
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dialog_Select_Music,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" } } }
        });

        if (result != null && result.Count == 1)
        {
            _audioService.StopMusic();
            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            
            // AssetType statt MediaFileType
            var asset = await _fileService.ImportAssetAsync(sourceFile, item, nodePath, AssetType.Music);

            if (asset != null)
            {
                MarkLibraryDirty();
                
                // Play logic needs full path
                var fullPath = AppPaths.ResolveDataPath(asset.RelativePath);
                
                _ = _audioService.PlayMusicAsync(fullPath);
                await SaveData();
            }
        }
    }

    private async Task ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return; 
    
        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        
        vm.OnResultSelected += async (result) => 
        {
            // Simple Conflict Resolution with User Confirmation
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

            // Änderungen am Item -> dirty
            MarkLibraryDirty();
            
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            
            // Downloads, Imports und Save in Background-Task verschieben (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    // DownloadAndSetAsset nutzt jetzt AssetType
                    if (!string.IsNullOrEmpty(result.CoverUrl)) 
                        await DownloadAndSetAsset(result.CoverUrl, item, nodePath, AssetType.Cover);
                
                    if (!string.IsNullOrEmpty(result.WallpaperUrl)) 
                        await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, AssetType.Wallpaper);

                    // Weitere Assets, falls vorhanden (z. B. Logo)...

                    await SaveData(); // Speichern im Background
                }
                catch (Exception ex)
                {
                    // Logge den Error (z. B. zeige später eine Notification oder Console)
                    Console.WriteLine($"Background scraping error: {ex.Message}");
                    // Optional: Invoke auf UI für eine MessageBox, falls kritisch
                }
            });
            
            // Close dialog manually if needed (finding window by DataContext)
            if (owner.OwnedWindows.FirstOrDefault(w => w.DataContext == vm) is Window dlg) dlg.Close();
        };

        var dialog = new ScrapeDialogView { DataContext = vm };
        await dialog.ShowDialog(owner);
    }

    private async Task ScrapeNodeAsync(MediaNode? node)
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
    
            var changed = false;
            
            if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description))
            {
                item.Description = result.Description;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer))
            {
                item.Developer = result.Developer;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre))
            {
                item.Genre = result.Genre;
                changed = true;
            }

            if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue)
            {
                item.ReleaseDate = result.ReleaseDate;
                changed = true;
            }

            if (item.Rating == 0 && result.Rating.HasValue)
            {
                item.Rating = result.Rating.Value;
                changed = true;
            }
    
            // Wir laden einfach runter und fügen hinzu. Der FileService kümmert sich um Dubletten/Nummerierung.
            if (!string.IsNullOrEmpty(result.CoverUrl))
            {
                // Prüfen wir noch, ob schon ein Cover da ist?
                // Mit dem neuen System können wir einfach hinzufügen (ergibt dann Cover_02)
                // oder prüfen ob Assets.Any(a => a.Type == AssetType.Cover)
                if (!item.Assets.Any(a => a.Type == AssetType.Cover))
                {
                    await DownloadAndSetAsset(result.CoverUrl, item, nodePath, AssetType.Cover);
                    changed = true;
                }
            }
            if (!string.IsNullOrEmpty(result.WallpaperUrl))
            {
                if (!item.Assets.Any(a => a.Type == AssetType.Wallpaper))
                {
                    await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, AssetType.Wallpaper);
                    changed = true;
                }
            }
            
            if (changed)
                MarkLibraryDirty();
        };
    
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private async Task DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, AssetType type)
    {
        try
        {
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            
            // Move statt Copy, da TempFile erstellt wurde
            // Achtung: Path.GetTempFileName erstellt eine 0-Byte Datei, Move wirft Fehler wenn Ziel existiert.
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;
            // AsyncImageHelper sollte angepasst sein oder wir nutzen ihn so:
            if (await AsyncImageHelper.SaveCachedImageAsync(url, tempPathWithExt)) 
            {
                success = true;
            }
            else
            {
                try
                {
                    // Use DI-managed HttpClient (configured in App: timeout + user-agent)
                    var data = await _httpClient.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempPathWithExt, data);
                    success = true;
                }
                catch (Exception ex) 
                { 
                    Debug.WriteLine($"Download Failed: {ex.Message}"); 
                }
            }

            if (success)
            {
                await _fileService.ImportAssetAsync(tempPathWithExt, item, nodePath, type);
                MarkLibraryDirty();
            }
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"Critical Download Error: {ex.Message}"); 
        }
    }

    private void OpenIntegratedSearch()
    {
        _selectedNode = null;
        // Force refresh of selection
        OnPropertyChanged(nameof(SelectedNode));
        
        var searchVm = new SearchAreaViewModel(RootItems) { ItemWidth = ItemWidth };
        searchVm.RequestPlay += item => {_ = PlayMediaAsync(item); }; // Now using Async method
        
        searchVm.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem))
            {
                var item = searchVm.SelectedMediaItem;
                 
                // Musik-Logik mit Helper und Assets
                var musicAsset = item?.GetPrimaryAssetPath(AssetType.Music);
                if (!string.IsNullOrEmpty(musicAsset))
                    _ = _audioService.PlayMusicAsync(AppPaths.ResolveDataPath(musicAsset));
                else 
                    _audioService.StopMusic();
            }
        };
        SelectedNodeContent = searchVm;
    }

    // Wrappers for Assets
    private async Task SetCoverAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Cover, AssetType.Cover);
    private async Task SetLogoAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Logo, AssetType.Logo);
    private async Task SetWallpaperAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Wallpaper, AssetType.Wallpaper);

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
        // Offload recursion and filesystem scanning to a background thread.
        await Task.Run(async () => 
        { 
            foreach (var rootNode in RootItems) 
            {
                await RescanNodeRecursive(rootNode); 
            }
        });
    }

    private async Task RescanNodeRecursive(MediaNode node)
    {
        var nodePath = PathHelper.GetNodePath(node, RootItems);
        
        // 1) Scan assets off the UI thread (filesystem only).
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(node.Items.Count);

            foreach (var item in node.Items)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        });

        // 2) Apply results on UI thread in batches to keep the UI responsive for very large nodes.
        const int batchSize = 500;
        for (int i = 0; i < scanned.Count; i += batchSize)
        {
            var start = i;
            var count = Math.Min(batchSize, scanned.Count - start);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int j = start; j < start + count; j++)
                {
                    var (item, assets) = scanned[j];

                    item.Assets.Clear();
                    foreach (var asset in assets)
                        item.Assets.Add(asset);
                }
            });

            // Yield between batches so the UI can process input/layout/render.
            await Task.Yield();
        }

        // 3) Recurse children
        foreach (var child in node.Children)
        {
            await RescanNodeRecursive(child);
        }
    }
    
    private async Task SetAssetAsync(MediaItem? item, string title, AssetType type)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;
        
        var result = await (StorageProvider ?? owner.StorageProvider).OpenFilePickerAsync(new FilePickerOpenOptions 
        { 
            Title = title, 
            AllowMultiple = false, 
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } 
        });
        
        if (result != null && result.Count == 1)
        {
            var asset = await _fileService.ImportAssetAsync(
                result[0].Path.LocalPath,
                item,
                PathHelper.GetNodePath(SelectedNode, RootItems),
                type);

            if (asset != null)
            {
                MarkLibraryDirty();
                await SaveData();
            }
        }
    }
}