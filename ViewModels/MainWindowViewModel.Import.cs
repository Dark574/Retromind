using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // Regex to detect Disk/Disc/Side/Part suffixes
    // Matches: " (Disk 1)", "_Disk1", " (Side A)", " - CD 1", etc.
    private static readonly System.Text.RegularExpressions.Regex MultiDiscRegex =
        // Require a clear separator (start/space/_/-/bracket) before Disc/Side tokens to avoid
        // matching inside words like "Unterirdische" (contains "disc").
        new(@"(?:^|[\s_\-]|\(|\[)\s*(?<kind>Disk|Disc|CD|Side|Part)\s*(?<token>[0-9A-H]+)(?:\s*(?:\)|\]))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int? ParseDiscIndex(string token)
    {
        // Supports: "1", "2", ... and "A".."H" (Side A/B, etc.)
        if (int.TryParse(token, out var n) && n > 0)
            return n;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'H')
                return (c - 'A') + 1;
        }

        return null;
    }

    private static string? BuildDiscLabel(string kind, string token, int? index)
    {
        // Keep labels user-friendly and stable for UI and playlist readability.
        kind = kind.Trim();

        if (string.Equals(kind, "Side", StringComparison.OrdinalIgnoreCase) && token.Length == 1)
        {
            var side = char.ToUpperInvariant(token[0]);
            if (side is >= 'A' and <= 'H')
                return $"Side {side}";
        }

        if (!string.IsNullOrWhiteSpace(token))
            return $"{kind} {token}";

        if (index.HasValue)
            return $"{kind} {index.Value}";

        return null;
    }

    private static (int? Index, string? Label) TryGetDiscInfoFromFileName(string fileNameWithoutExtension)
    {
        var match = MultiDiscRegex.Match(fileNameWithoutExtension);
        if (!match.Success)
            return (null, null);

        var kind = match.Groups["kind"].Value.Trim();
        var token = match.Groups["token"].Value.Trim();

        var idx = ParseDiscIndex(token);
        var label = BuildDiscLabel(kind, token, idx);

        return (idx, label);
    }
    
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

        if (!importedItems.Any()) return;
        
        // Snapshot node path once (stable, avoids repeated computation)
        var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var defaultMediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
            ? MediaType.Native
            : MediaType.Emulator;

        // 1) Decide what to add (no UI mutations)
        var itemsToAdd = importedItems
            .Where(item =>
            {
                var incoming = item.GetPrimaryLaunchPath();
                if (string.IsNullOrWhiteSpace(incoming))
                    return false;

                return !targetNode.Items.Any(existing =>
                    string.Equals(existing.GetPrimaryLaunchPath(), incoming, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (itemsToAdd.Count == 0) return;

        if (defaultMediaType == MediaType.Emulator)
        {
            foreach (var item in itemsToAdd)
                item.MediaType = MediaType.Emulator;
        }

        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        // 2) Scan assets off the UI thread (filesystem only)
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(itemsToAdd.Count);

            foreach (MediaItem item in itemsToAdd)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((Item: item, Assets: assets));
            }

            return list;
        });

        // 3) Apply everything on UI thread (Items/Assets/Sort)
        await UiThreadHelper.InvokeAsync(() =>
        {
            var newItems = scanned.Select(entry => entry.Item).ToList();
            InsertMediaItemsOptimized(targetNode.Items, newItems);

            foreach (var (item, assets) in scanned)
            {
                item.Assets.Clear();
                foreach (var asset in assets)
                    item.Assets.Add(asset);
            }

            MarkLibraryDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        // 4) Persist (IO; ok to await from here)
        await SaveData();
    }
    
    private async Task ImportSteamAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var discoveredSteamApps = new List<string>();
        var items = await _storeService.ImportSteamGamesAsync(discoveredSteamAppsPaths: discoveredSteamApps);
        if (items.Count == 0)
        {
            var tryManual = await ShowConfirmDialog(owner, Strings.Dialog_NoSteamGamesFound_SelectPath);
            if (!tryManual) return;

            var storageProvider = StorageProvider ?? owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Dialog_SelectSteamLibraryFolder,
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var manualPath = folders[0].Path.LocalPath;
            discoveredSteamApps.Clear();
            items = await _storeService.ImportSteamGamesAsync(manualPath, discoveredSteamApps);

            if (items.Count == 0)
            {
                await ShowConfirmDialog(owner, Strings.Dialog_NoSteamGamesFound);
                return;
            }
        }

        var message = string.Format(Strings.Dialog_ConfirmImportSteamFormat, items.Count, targetNode.Name);
        if (!await ShowConfirmDialog(owner, message))
            return;

        StoreSteamLibraryPaths(discoveredSteamApps);

        // Determine adds off-thread-safe (pure checks)
        var itemsToAdd = items
            .Where(item => !targetNode.Items.Any(x => x.Title == item.Title))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            MarkLibraryDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private async Task ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var discoveredHeroicConfigs = new List<string>();
        var items = await _storeService.ImportHeroicGogAsync(discoveredConfigPaths: discoveredHeroicConfigs);
        if (items.Count == 0)
        {
            var tryManual = await ShowConfirmDialog(owner, Strings.Dialog_NoGogInstallationsFound_SelectPath);
            if (!tryManual) return;

            var storageProvider = StorageProvider ?? owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Dialog_SelectHeroicGogFolder,
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var manualPath = folders[0].Path.LocalPath;

            discoveredHeroicConfigs.Clear();
            items = await _storeService.ImportHeroicGogAsync(manualPath, discoveredHeroicConfigs);

            if (items.Count == 0)
            {
                await ShowConfirmDialog(owner, Strings.Dialog_NoGogInstallationsFound);
                return;
            }
        }

        var message = string.Format(Strings.Dialog_ConfirmImportGogFormat, items.Count);
        if (!await ShowConfirmDialog(owner, message))
            return;

        StoreHeroicGogConfigPaths(discoveredHeroicConfigs);

        var itemsToAdd = items
            .Where(item => !targetNode.Items.Any(x => x.Title == item.Title))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            MarkLibraryDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private async Task ImportEpicAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var discoveredHeroicConfigs = new List<string>();
        var items = await _storeService.ImportHeroicEpicAsync(discoveredConfigPaths: discoveredHeroicConfigs);
        if (items.Count == 0)
        {
            var tryManual = await ShowConfirmDialog(owner, Strings.Dialog_NoEpicInstallationsFound_SelectPath);
            if (!tryManual) return;

            var storageProvider = StorageProvider ?? owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Dialog_SelectHeroicEpicFolder,
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var manualPath = folders[0].Path.LocalPath;

            discoveredHeroicConfigs.Clear();
            items = await _storeService.ImportHeroicEpicAsync(manualPath, discoveredHeroicConfigs);

            if (items.Count == 0)
            {
                await ShowConfirmDialog(owner, Strings.Dialog_NoEpicInstallationsFound);
                return;
            }
        }

        var message = string.Format(Strings.Dialog_ConfirmImportEpicFormat, items.Count);
        if (!await ShowConfirmDialog(owner, message))
            return;

        StoreHeroicEpicConfigPaths(discoveredHeroicConfigs);

        var itemsToAdd = items
            .Where(item => !targetNode.Items.Any(x => x.Title == item.Title))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            MarkLibraryDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private void StoreHeroicGogConfigPaths(IEnumerable<string> configPaths)
    {
        if (configPaths == null)
            return;

        _currentSettings.HeroicGogConfigPaths ??= new List<string>();

        var existing = new HashSet<string>(_currentSettings.HeroicGogConfigPaths, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in configPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(fullPath), "installed.json", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(fullPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    fullPath = parent;
            }

            if (existing.Add(fullPath))
            {
                _currentSettings.HeroicGogConfigPaths.Add(fullPath);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsOnly();
    }

    private void StoreHeroicEpicConfigPaths(IEnumerable<string> configPaths)
    {
        if (configPaths == null)
            return;

        _currentSettings.HeroicEpicConfigPaths ??= new List<string>();

        var existing = new HashSet<string>(_currentSettings.HeroicEpicConfigPaths, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in configPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(fullPath), "installed.json", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(fullPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    fullPath = parent;
            }

            if (existing.Add(fullPath))
            {
                _currentSettings.HeroicEpicConfigPaths.Add(fullPath);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsOnly();
    }

    private void StoreSteamLibraryPaths(IEnumerable<string> steamAppsPaths)
    {
        if (steamAppsPaths == null)
            return;

        _currentSettings.SteamLibraryPaths ??= new List<string>();

        var existing = new HashSet<string>(_currentSettings.SteamLibraryPaths, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in steamAppsPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (existing.Add(fullPath))
            {
                _currentSettings.SteamLibraryPaths.Add(fullPath);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsOnly();
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

        if (result == null || result.Count == 0) return;

        var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var defaultMediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
            ? MediaType.Native
            : MediaType.Emulator;

        static bool TryMakeDataRelativeIfInsideDataRoot(string absolutePath, out string relativePath)
        {
            relativePath = string.Empty;

            if (string.IsNullOrWhiteSpace(absolutePath))
                return false;

            if (!Path.IsPathRooted(absolutePath))
                return false;

            var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
            var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
                ? dataRoot
                : dataRoot + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(absolutePath);

            if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal) ||
                fullPath.StartsWith(dataRootWithSep, StringComparison.Ordinal))
            {
                relativePath = Path.GetRelativePath(dataRoot, fullPath);
                return true;
            }

            return false;
        }

        // 1) Build new items (UI-free)
        var itemsToAdd = new List<MediaItem>(result.Count);

        var usePortablePaths = _currentSettings.PreferPortableLaunchPaths;

        if (result.Count > 1)
        {
            var combine = await ShowConfirmDialog(owner,
                string.Format(Strings.Dialog_ConfirmCombineMultiDiscFormat, result.Count));

            if (combine)
            {
                var first = result[0];
                var rawTitle = Path.GetFileNameWithoutExtension(first.Name);
                var title = await PromptForName(owner, $"{Strings.Common_Title} '{first.Name}':") ?? rawTitle;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                // Build file refs with "smart" disc detection from filenames
                var temp = result
                    .Select(f =>
                    {
                        var baseName = Path.GetFileNameWithoutExtension(f.Name);
                        var (idx, label) = TryGetDiscInfoFromFileName(baseName);
                        return (File: f, Index: idx, Label: label);
                    })
                    .ToList();

                // Stable ordering: Index ascending when available, else by filename
                var ordered = temp
                    .OrderBy(x => x.Index ?? int.MaxValue)
                    .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var files = new List<MediaFileRef>(ordered.Count);
                for (int i = 0; i < ordered.Count; i++)
                {
                    var fallbackIndex = i + 1;
                    var rawPath = ordered[i].File.Path.LocalPath;
                    var storedPath = rawPath;
                    var storedKind = MediaFileKind.Absolute;

                    if (usePortablePaths &&
                        TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
                    {
                        storedPath = relativePath;
                        storedKind = MediaFileKind.LibraryRelative;
                    }

                    files.Add(new MediaFileRef
                    {
                        Kind = storedKind,
                        Path = storedPath,
                        Index = ordered[i].Index ?? fallbackIndex,
                        Label = ordered[i].Label ?? $"Disc {fallbackIndex}"
                    });
                }

                itemsToAdd.Add(new MediaItem
                {
                    Title = title,
                    Files = files,
                    MediaType = defaultMediaType
                });
            }
            else
            {
                foreach (var file in result)
                {
                    var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
                    var title = await PromptForName(owner, $"{Strings.Common_Title} '{file.Name}':") ?? rawTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
                    var rawPath = file.Path.LocalPath;
                    var storedPath = rawPath;
                    var storedKind = MediaFileKind.Absolute;

                    if (usePortablePaths &&
                        TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
                    {
                        storedPath = relativePath;
                        storedKind = MediaFileKind.LibraryRelative;
                    }

                    itemsToAdd.Add(new MediaItem
                    {
                        Title = title,
                        Files = new List<MediaFileRef>
                        {
                            new()
                            {
                                Kind = storedKind,
                                Path = storedPath,
                                Index = 1,
                                Label = "Disc 1"
                            }
                        },
                        MediaType = defaultMediaType
                    });
                }
            }
        }
        else
        {
            var file = result[0];
            var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
            var title = await PromptForName(owner, $"{Strings.Common_Title} '{file.Name}':") ?? rawTitle;
            if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
            var rawPath = file.Path.LocalPath;
            var storedPath = rawPath;
            var storedKind = MediaFileKind.Absolute;

            if (usePortablePaths &&
                TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
            {
                storedPath = relativePath;
                storedKind = MediaFileKind.LibraryRelative;
            }

            itemsToAdd.Add(new MediaItem
            {
                Title = title,
                Files = new List<MediaFileRef>
                {
                    new()
                    {
                        Kind = storedKind,
                        Path = storedPath,
                        Index = 1,
                        Label = "Disc 1"
                    }
                },
                MediaType = defaultMediaType
            });
        }

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        // 2) Scan assets off-thread (filesystem only)
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(itemsToAdd.Count);

            foreach (var item in itemsToAdd)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        });

        // Keep the list of actually inserted items outside the UI callback
        var newlyAddedItems = new List<MediaItem>(scanned.Count);
                
        // 3) Apply on UI thread: add items + assets
        await UiThreadHelper.InvokeAsync(() =>
        {
            var newItems = scanned.Select(entry => entry.Item).ToList();
            InsertMediaItemsOptimized(targetNode.Items, newItems);

            foreach (var (item, assets) in scanned)
            {
                newlyAddedItems.Add(item);

                item.Assets.Clear();
                foreach (var asset in assets)
                    item.Assets.Add(asset);
            }

            MarkLibraryDirty();
        });

        // 4) Remember the last created item as the "selectable" ID
        if (newlyAddedItems.Count > 0)
        {
            var lastItem = newlyAddedItems[^1];
            _currentSettings.LastSelectedMediaId = lastItem.Id;
            _lastSelectedMediaByNodeId[targetNode.Id] = lastItem.Id;
            SaveSettingsOnly();
        }
        
        // 5) Refresh the central view only if this node is currently shown
        if (IsNodeInCurrentView(targetNode))
        {
            // Wait until SelectedNodeContent is actually rebuilt
            await UpdateContentAsync();
        }

        // 6) Persist to disk (library + settings)
        await SaveData();
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
        var editVm = new EditMediaViewModel(item, _currentSettings, _fileService, nodePath, inherited, RootItems, parentNode) 
        { 
            StorageProvider = StorageProvider ?? owner.StorageProvider 
        };
        
        var dialog = new EditMediaView { DataContext = editVm };
        
        // bool? so that "X" (no result) remains clearly identifiable
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
        
        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dialog_Select_Music,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac", "*.sid" } } }
        });

        if (result != null && result.Count == 1)
        {
            _audioService.StopMusic();
            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(parentNode, RootItems);
            
            // Use AssetType.Music instead of a media file kind enum.
            var asset = await _fileService.ImportAssetAsync(sourceFile, item, nodePath, AssetType.Music);

            if (asset != null)
            {
                await UiThreadHelper.InvokeAsync(() => item.Assets.Add(asset));

                MarkLibraryDirty();

                var fullPath = AppPaths.ResolveDataPath(asset.RelativePath);
                _ = _audioService.PlayMusicAsync(fullPath);

                await SaveData();
            }
        }
    }

    private async Task ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;

        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;

        var nodePath = PathHelper.GetNodePath(parentNode, RootItems);
        var importSettings = GetScraperImportSettings();

        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        
        vm.OnResultSelectedAsync += async (result) => 
        {
            var changed = false;

            if (await ApplyScrapedMetadataAsync(owner, item, result, importSettings, allowConflictPrompts: true))
                changed = true;

            if (await ApplyScrapedAssetsAsync(
                    owner,
                    item,
                    result,
                    nodePath,
                    importSettings,
                    allowConflictPromptsForAssets: true,
                    appendAssetsOnConflictWithoutPrompt: false))
                changed = true;

            if (changed)
            {
                MarkLibraryDirty();
                await SaveData();
            }
            
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
        var importSettings = GetScraperImportSettings();

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService);
        vm.OnItemScrapedAsync = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
    
            var changed = false;

            if (await ApplyScrapedMetadataAsync(owner, item, result, importSettings, allowConflictPrompts: false))
                changed = true;

            if (await ApplyScrapedAssetsAsync(
                    owner,
                    item,
                    result,
                    nodePath,
                    importSettings,
                    allowConflictPromptsForAssets: false,
                    appendAssetsOnConflictWithoutPrompt: importSettings.AppendAssetsDuringBulkScrape))
                changed = true;
            
            if (changed)
                MarkLibraryDirty();
        };
    
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private ScraperImportSettings GetScraperImportSettings()
    {
        _currentSettings.ScraperImport ??= new ScraperImportSettings();
        return _currentSettings.ScraperImport;
    }

    private async Task<bool> ApplyScrapedMetadataAsync(
        Window owner,
        MediaItem item,
        ScraperSearchResult result,
        ScraperImportSettings settings,
        bool allowConflictPrompts)
    {
        var changed = false;
        var mode = settings.ExistingDataMode;

        if (settings.ImportDescription &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Description", "Description"),
                item.Description,
                result.Description,
                value => item.Description = value,
                allowConflictPrompts,
                mode,
                StringComparison.Ordinal))
        {
            changed = true;
        }

        if (settings.ImportReleaseDate &&
            await TryApplyDateFieldAsync(
                owner,
                T("Common.ReleaseDate", "Release Date"),
                item.ReleaseDate,
                result.ReleaseDate,
                value => item.ReleaseDate = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportRating &&
            await TryApplyRatingFieldAsync(
                owner,
                T("Common.Rating", "Rating"),
                item.Rating,
                result.Rating,
                value => item.Rating = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportDeveloper &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Developer", "Developer"),
                item.Developer,
                result.Developer,
                value => item.Developer = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportGenre &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Genre", "Genre"),
                item.Genre,
                result.Genre,
                value => item.Genre = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlatform)
        {
            if (await TryApplyStringFieldAsync(
                    owner,
                    T("Common.Platform", "Platform"),
                    item.Platform,
                    result.Platform,
                    value => item.Platform = value,
                    allowConflictPrompts,
                    mode))
            {
                changed = true;
            }

            var platform = result.Platform?.Trim();
            if (!string.IsNullOrWhiteSpace(platform) &&
                string.Equals(item.Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase) &&
                !item.Tags.Any(t => string.Equals(t, platform, StringComparison.OrdinalIgnoreCase)))
            {
                item.Tags.Add(platform);
                changed = true;
            }
        }

        if (settings.ImportPublisher &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Publisher", "Publisher"),
                item.Publisher,
                result.Publisher,
                value => item.Publisher = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSeries &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Series", "Series"),
                item.Series,
                result.Series,
                value => item.Series = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportReleaseType &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.ReleaseType", "Release Type"),
                item.ReleaseType,
                result.ReleaseType,
                value => item.ReleaseType = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSortTitle &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.SortTitle", "Sort Title"),
                item.SortTitle,
                result.SortTitle,
                value => item.SortTitle = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlayMode &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.PlayMode", "Play Mode"),
                item.PlayMode,
                result.PlayMode,
                value => item.PlayMode = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportMaxPlayers &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.MaxPlayers", "Max Players"),
                item.MaxPlayers,
                result.MaxPlayers,
                value => item.MaxPlayers = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSource &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Source", "Source"),
                item.Source,
                result.Source,
                value => item.Source = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportCustomFields &&
            await MergeCustomFieldsAsync(owner, item, result.CustomFields, allowConflictPrompts, mode))
        {
            changed = true;
        }

        return changed;
    }

    private async Task<bool> ApplyScrapedAssetsAsync(
        Window owner,
        MediaItem item,
        ScraperSearchResult result,
        List<string> nodePath,
        ScraperImportSettings settings,
        bool allowConflictPromptsForAssets,
        bool appendAssetsOnConflictWithoutPrompt)
    {
        var changed = false;
        var mode = settings.ExistingDataMode;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Cover, result.CoverUrl, settings.ImportCover, mode, T("Button.Cover", "Cover"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Wallpaper, result.WallpaperUrl, settings.ImportWallpaper, mode, T("NodeSettings_ArtworkWallpaperLabel", "Wallpaper"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Screenshot, result.ScreenshotUrl, settings.ImportScreenshot, mode, T("Button.Screenshot", "Screenshot"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Logo, result.LogoUrl, settings.ImportLogo, mode, T("Button.Logo", "Logo"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Marquee, result.MarqueeUrl, settings.ImportMarquee, mode, T("NodeSettings_ArtworkMarqueeLabel", "Marquee"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Bezel, result.BezelUrl, settings.ImportBezel, mode, T("Button.Bezel", "Bezel"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.ControlPanel, result.ControlPanelUrl, settings.ImportControlPanel, mode, T("Button.ControlPanel", "Control panel"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        return changed;
    }

    private async Task<bool> TryImportScrapedAssetAsync(
        Window owner,
        MediaItem item,
        List<string> nodePath,
        AssetType type,
        string? url,
        bool isEnabled,
        ScraperExistingDataMode mode,
        string assetLabel,
        bool allowConflictPromptsForAssets,
        bool appendAssetsOnConflictWithoutPrompt)
    {
        if (!isEnabled || string.IsNullOrWhiteSpace(url))
            return false;

        var hasExisting = item.Assets.Any(a => a.Type == type);
        if (hasExisting)
        {
            if (!allowConflictPromptsForAssets)
                return appendAssetsOnConflictWithoutPrompt && await DownloadAndSetAsset(url, item, nodePath, type);

            if (mode == ScraperExistingDataMode.OnlyMissing)
                return false;

            if (mode == ScraperExistingDataMode.AskOnConflict)
            {
                var message = string.Format(
                    T("Dialog.Scraper.AssetConflictFormat", "Item already has {0}. Add another one?"),
                    assetLabel);

                if (!await ShowConfirmDialog(owner, message))
                    return false;
            }
        }

        return await DownloadAndSetAsset(url, item, nodePath, type);
    }

    private async Task<bool> TryApplyStringFieldAsync(
        Window owner,
        string fieldLabel,
        string? currentValue,
        string? incomingValue,
        Action<string> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var incoming = incomingValue?.Trim();
        if (string.IsNullOrWhiteSpace(incoming))
            return false;

        var current = currentValue?.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            applyValue(incoming);
            return true;
        }

        if (string.Equals(current, incoming, comparison))
            return false;

        if (!await ShouldOverwriteExistingAsync(owner, fieldLabel, current, incoming, allowConflictPrompts, mode))
            return false;

        applyValue(incoming);
        return true;
    }

    private async Task<bool> TryApplyDateFieldAsync(
        Window owner,
        string fieldLabel,
        DateTime? currentValue,
        DateTime? incomingValue,
        Action<DateTime?> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = incomingValue.Value.Date;
        if (!currentValue.HasValue)
        {
            applyValue(incoming);
            return true;
        }

        var current = currentValue.Value.Date;
        if (current == incoming)
            return false;

        if (!await ShouldOverwriteExistingAsync(
                owner,
                fieldLabel,
                current.ToShortDateString(),
                incoming.ToShortDateString(),
                allowConflictPrompts,
                mode))
        {
            return false;
        }

        applyValue(incoming);
        return true;
    }

    private async Task<bool> TryApplyRatingFieldAsync(
        Window owner,
        string fieldLabel,
        double currentValue,
        double? incomingValue,
        Action<double> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = Math.Clamp(incomingValue.Value, 0d, 100d);
        var hasCurrent = currentValue > 0d;
        if (!hasCurrent)
        {
            applyValue(incoming);
            return true;
        }

        if (Math.Abs(currentValue - incoming) < 0.0001d)
            return false;

        if (!await ShouldOverwriteExistingAsync(
                owner,
                fieldLabel,
                currentValue.ToString("0.##"),
                incoming.ToString("0.##"),
                allowConflictPrompts,
                mode))
        {
            return false;
        }

        applyValue(incoming);
        return true;
    }

    private async Task<bool> MergeCustomFieldsAsync(
        Window owner,
        MediaItem item,
        Dictionary<string, string>? incoming,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (incoming == null || incoming.Count == 0)
            return false;

        var merged = new Dictionary<string, string>(
            item.CustomFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var changed = false;

        foreach (var kv in incoming)
        {
            var key = kv.Key?.Trim();
            var value = kv.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (!merged.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                merged[key] = value;
                changed = true;
                continue;
            }

            if (string.Equals(existing, value, StringComparison.Ordinal))
                continue;

            if (mode == ScraperExistingDataMode.OnlyMissing)
                continue;

            if (mode == ScraperExistingDataMode.AskOnConflict)
            {
                if (!allowConflictPrompts)
                    continue;

                var message = string.Format(
                    T("Dialog.Scraper.CustomFieldConflictFormat", "Update custom field '{0}'? Current: '{1}' | New: '{2}'"),
                    key,
                    BuildConflictPreview(existing),
                    BuildConflictPreview(value));

                if (!await ShowConfirmDialog(owner, message))
                    continue;
            }

            merged[key] = value;
            changed = true;
        }

        if (!changed)
            return false;

        item.CustomFields = merged;
        return true;
    }

    private async Task<bool> ShouldOverwriteExistingAsync(
        Window owner,
        string fieldLabel,
        string currentValue,
        string incomingValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (mode == ScraperExistingDataMode.OverwriteAlways)
            return true;

        if (mode == ScraperExistingDataMode.OnlyMissing)
            return false;

        if (!allowConflictPrompts)
            return false;

        var message = string.Format(
            T("Dialog.Scraper.FieldConflictFormat", "Update {0}? Current: '{1}' | New: '{2}'"),
            fieldLabel.Trim().TrimEnd(':'),
            BuildConflictPreview(currentValue),
            BuildConflictPreview(incomingValue));

        return await ShowConfirmDialog(owner, message);
    }

    private static string BuildConflictPreview(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 80
            ? compact
            : compact.Substring(0, 80) + "...";
    }

    private async Task<bool> DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, AssetType type)
    {
        string? tempPathWithExt = null;
        try
        {
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;

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
                var imported = await _fileService.ImportAssetAsync(tempPathWithExt, item, nodePath, type);
                if (imported != null)
                {
                    await UiThreadHelper.InvokeAsync(() => item.Assets.Add(imported));
                    return true;
                }
            }
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"Critical Download Error: {ex.Message}"); 
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(tempPathWithExt) && File.Exists(tempPathWithExt))
                    File.Delete(tempPathWithExt);
            }
            catch
            {
                // best effort cleanup
            }
        }

        return false;
    }

    private void OpenIntegratedSearch()
    {
        // Toggle behavior: pressing search while search is already open jumps back.
        if (SelectedNodeContent is SearchAreaViewModel activeSearchVm)
        {
            CloseIntegratedSearch(activeSearchVm);
            return;
        }

        // Remember where the user came from so we can restore it on next toggle.
        _searchReturnNodeId = SelectedNode?.Id;
        _searchReturnItemId = _currentMediaAreaVm?.SelectedMediaItem?.Id;

        // Ensure any previous media-area handlers are detached before switching views.
        DetachMediaAreaHandlers();
        DetachSearchAreaHandlers();

        _selectedNode = null;
        // Force refresh of selection
        OnPropertyChanged(nameof(SelectedNode));
        
        var searchVm = new SearchAreaViewModel(RootItems, IsParentalFilterActive) { ItemWidth = ItemWidth };
        searchVm.RequestPlay += item => {_ = PlayMediaAsync(item); }; // Now using Async method
        
        searchVm.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(SearchAreaViewModel.ItemWidth))
            {
                ItemWidth = searchVm.ItemWidth;
                SaveSettingsOnly();
                return;
            }

            if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem))
            {
                var item = searchVm.SelectedMediaItem;

                OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));

                if (!_currentSettings.EnableSelectionMusicPreview)
                {
                    _audioService.StopMusic();
                    return;
                }

                // Music logic with helper and assets
                var musicAsset = item?.GetPrimaryAssetPath(AssetType.Music);
                if (!string.IsNullOrEmpty(musicAsset))
                    _ = _audioService.PlayMusicAsync(AppPaths.ResolveDataPath(musicAsset));
                else
                    _audioService.StopMusic();
            }
        };
        SelectedNodeContent = searchVm;
    }

    private void CloseIntegratedSearch(SearchAreaViewModel searchVm)
    {
        var selectedSearchItemId = searchVm.SelectedMediaItem?.Id;
        var desiredItemId = selectedSearchItemId ?? _searchReturnItemId;

        MediaNode? targetNode = null;

        if (!string.IsNullOrWhiteSpace(selectedSearchItemId))
            TryFindNodeByMediaId(RootItems, selectedSearchItemId, out targetNode);

        if (targetNode == null && !string.IsNullOrWhiteSpace(_searchReturnNodeId))
            targetNode = FindNodeById(RootItems, _searchReturnNodeId);

        if (targetNode == null && !string.IsNullOrWhiteSpace(desiredItemId))
            TryFindNodeByMediaId(RootItems, desiredItemId, out targetNode);

        if (targetNode != null && !targetNode.IsVisibleInTree)
            targetNode = null;

        if (targetNode == null)
            targetNode = FindFirstVisibleNode();

        // Leave search mode now (dispose search VM) before restoring content.
        DetachSearchAreaHandlers();

        // Clear remembered return state once the toggle-back was requested.
        _searchReturnNodeId = null;
        _searchReturnItemId = null;

        if (targetNode == null)
        {
            _selectedNode = null;
            OnPropertyChanged(nameof(SelectedNode));
            SelectedNodeContent = null;
            _audioService.StopMusic();

            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            return;
        }

        if (!string.IsNullOrWhiteSpace(desiredItemId))
            _lastSelectedMediaByNodeId[targetNode.Id] = desiredItemId;

        ExpandPathToNode(RootItems, targetNode);
        SelectedNode = targetNode;
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

    private string? ResolveEffectiveDefaultEmulatorId(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems);
        chain.Reverse();
        return chain.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.DefaultEmulatorId))?.DefaultEmulatorId;
    }

    private void ApplyEffectiveDefaultEmulator(MediaNode targetNode, IEnumerable<MediaItem> items)
    {
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        if (string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId))
            return;

        foreach (var item in items)
        {
            if (item.MediaType != MediaType.Native)
                continue;

            if (!string.IsNullOrWhiteSpace(item.EmulatorId))
                continue;

            if (!string.IsNullOrWhiteSpace(item.LauncherPath))
                continue;

            item.MediaType = MediaType.Emulator;
        }
    }

    private async Task RescanAllAssetsAsync()
    {
        List<MediaNode> rootNodes = new();
        await UiThreadHelper.InvokeAsync(() =>
        {
            rootNodes = RootItems.ToList();
        });

        var anyChanged = false;

        // Offload recursion and filesystem scanning to a background thread.
        await Task.Run(async () => 
        { 
            foreach (var rootNode in rootNodes) 
            {
                if (await RescanNodeRecursive(rootNode))
                    anyChanged = true;
            }
        });

        if (anyChanged)
            MarkLibraryDirty();
    }

    private async Task<bool> RescanNodeRecursive(MediaNode node)
    {
        List<string> nodePath = new();
        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            nodePath = PathHelper.GetNodePath(node, RootItems);
            items = node.Items.ToList();
            children = node.Children.ToList();
        });
        
        // 1) Scan assets off the UI thread (filesystem only).
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(items.Count);

            foreach (var item in items)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        });

        var changed = false;

        // 2) Apply results on UI thread in batches to keep the UI responsive for very large nodes.
        const int batchSize = 500;
        for (int i = 0; i < scanned.Count; i += batchSize)
        {
            var start = i;
            var count = Math.Min(batchSize, scanned.Count - start);

            await UiThreadHelper.InvokeAsync(() =>
            {
                for (int j = start; j < start + count; j++)
                {
                    var (item, assets) = scanned[j];

                    if (AssetsMatch(item.Assets, assets))
                        continue;

                    changed = true;

                    item.Assets.Clear();
                    foreach (var asset in assets)
                        item.Assets.Add(asset);
                }
            });

            // Yield between batches so the UI can process input/layout/render.
            await Task.Yield();
        }

        // 3) Recurse children
        foreach (var child in children)
        {
            if (await RescanNodeRecursive(child))
                changed = true;
        }

        return changed;
    }

    private static bool AssetsMatch(IList<MediaAsset> existing, List<MediaAsset> scanned)
    {
        if (existing.Count != scanned.Count)
            return false;

        var set = new HashSet<(AssetType Type, string Path)>(existing.Count);
        foreach (var asset in existing)
        {
            var path = asset.RelativePath ?? string.Empty;
            set.Add((asset.Type, path));
        }

        foreach (var asset in scanned)
        {
            var path = asset.RelativePath ?? string.Empty;
            if (!set.Contains((asset.Type, path)))
                return false;
        }

        return true;
    }
    
    private async Task SetAssetAsync(MediaItem? item, string title, AssetType type)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;
        
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
                PathHelper.GetNodePath(parentNode, RootItems),
                type);

            if (asset != null)
            {
                await UiThreadHelper.InvokeAsync(() => item.Assets.Add(asset));
                MarkLibraryDirty();
                await SaveData();
            }
        }
    }
}
