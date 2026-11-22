using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.GameSystems;
using Retromind.Services.Scrapers;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private static IDisposable BeginBusyCursor(Window owner)
        => new BusyCursorScope(owner);
    
    // --- Import Actions ---

    private async Task PublishStoreImportAsync(MediaNode targetNode, IReadOnlyList<MediaItem> itemsToAdd)
    {
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);
        });

        var lastItem = itemsToAdd[^1];
        _currentSettings.LastSelectedMediaId = lastItem.Id;
        _desktopContent.RememberNodeSelection(targetNode.Id, lastItem.Id);
        SaveSettingsOnly();

        if (IsNodeInCurrentView(targetNode))
            await UpdateContentAsync();

        await SaveData();
    }

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

        var defaultExt = "iso,bin,cue,img,chd,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64,exe,sh";
        var extensionsStr = await PromptForName(
            owner,
            Strings.Dialog_FileExtensionsPrompt,
            initialText: defaultExt);
        if (extensionsStr == null) return;
        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // Snapshot node path once (stable, avoids repeated computation)
        var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var defaultMediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
            ? MediaType.Native
            : MediaType.Emulator;

        List<MediaItem> itemsToAdd = [];
        List<(MediaItem Item, List<MediaAsset> Assets)> scanned = [];
        using (var progressViewModel = new FolderImportProgressViewModel())
        {
            var progressDialog = new FolderImportProgressView
            {
                DataContext = progressViewModel
            };
            var dialogTask = progressDialog.ShowDialog(owner);
            var progress = new Progress<FolderImportProgress>(progressViewModel.Report);

            try
            {
                var importedItems = await _importService.ImportFromFolderAsync(
                    sourcePath,
                    extensions,
                    progress,
                    progressViewModel.Token);

                if (importedItems.Count == 0)
                    return;

                // Decide what to add without changing bound collections.
                itemsToAdd = importedItems
                    .Where(item =>
                    {
                        var incoming = item.GetPrimaryLaunchPath();
                        if (string.IsNullOrWhiteSpace(incoming))
                            return false;

                        return !targetNode.Items.Any(existing =>
                            FileSystemPathIdentity.Equals(existing.GetPrimaryLaunchPath(), incoming));
                    })
                    .ToList();

                if (itemsToAdd.Count == 0)
                    return;

                if (defaultMediaType == MediaType.Emulator)
                {
                    foreach (var item in itemsToAdd)
                        item.MediaType = MediaType.Emulator;
                }

                ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

                scanned = await Task.Run(
                    () => _fileService.ScanItemsAssets(
                        itemsToAdd,
                        nodePath,
                        progress,
                        progressViewModel.Token),
                    progressViewModel.Token);
                progressViewModel.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (progressViewModel.Token.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                progressViewModel.MarkFinished();
                progressDialog.CompleteAndClose();
                await dialogTask;
            }
        }

        // A configured game system makes imported ROMs immediately eligible for
        // RetroAchievements identification. Cancellation only stops identification;
        // the selected files are still imported normally.
        await IdentifyImportedRomsWithRetroAchievementsAsync(owner, targetNode, itemsToAdd);

        // Apply everything on the UI thread only after scanning completed.
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
            .Where(item => !targetNode.Items.Any(existing =>
                StoreGameIdentityHelper.IsSameGame(existing, item)))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private void StoreSteamLibraryPaths(IEnumerable<string> steamAppsPaths)
    {
        if (steamAppsPaths == null)
            return;

        _currentSettings.SteamLibraryPaths ??= new List<string>();

        var existing = new HashSet<string>(
            _currentSettings.SteamLibraryPaths,
            FileSystemPathIdentity.Comparer);
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
                var title = await PromptForName(
                    owner,
                    $"{Strings.Common_Title} '{first.Name}':",
                    initialText: rawTitle);
                if (title == null) return;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                // Build file refs with "smart" disc detection from filenames
                var temp = result
                    .Select(f =>
                    {
                        var baseName = Path.GetFileNameWithoutExtension(f.Name);
                        var (_, idx, label) = MultiDiscFileNameHelper.Parse(baseName);
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
                        PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
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
                    var title = await PromptForName(
                        owner,
                        $"{Strings.Common_Title} '{file.Name}':",
                        initialText: rawTitle);
                    if (title == null) return;
                    if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
                    var rawPath = file.Path.LocalPath;
                    var storedPath = rawPath;
                    var storedKind = MediaFileKind.Absolute;

                    if (usePortablePaths &&
                        PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
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
            var title = await PromptForName(
                owner,
                $"{Strings.Common_Title} '{file.Name}':",
                initialText: rawTitle);
            if (title == null) return;
            if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
            var rawPath = file.Path.LocalPath;
            var storedPath = rawPath;
            var storedKind = MediaFileKind.Absolute;

            if (usePortablePaths &&
                PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
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

        });

        // 4) Remember the last created item as the "selectable" ID
        if (newlyAddedItems.Count > 0)
        {
            var lastItem = newlyAddedItems[^1];
            _currentSettings.LastSelectedMediaId = lastItem.Id;
            _desktopContent.RememberNodeSelection(targetNode.Id, lastItem.Id);
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

    private async Task AddEmptyMediaAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode)
            targetNode = SelectedNode;

        if (targetNode == null || CurrentWindow is not { } owner)
            return;

        var title = await PromptForName(
            owner,
            Strings.Dialog_EnterName_Message,
            input => string.IsNullOrWhiteSpace(input)
                ? new NamePromptViewModel.NamePromptValidationResult(
                    false,
                    Strings.Dialog_NamePrompt_EmptyName)
                : new NamePromptViewModel.NamePromptValidationResult(true));

        if (string.IsNullOrWhiteSpace(title))
            return;

        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var item = new MediaItem
        {
            Title = title.Trim(),
            Files = new List<MediaFileRef>(),
            MediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
                ? MediaType.Native
                : MediaType.Emulator
        };

        ApplyEffectiveParentalProtection(targetNode, [item]);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, [item]);
        });

        _currentSettings.LastSelectedMediaId = item.Id;
        _desktopContent.RememberNodeSelection(targetNode.Id, item.Id);
        SaveSettingsOnly();

        if (IsNodeInCurrentView(targetNode))
            await UpdateContentAsync();

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
        var editVm = new EditMediaViewModel(
            item,
            _currentSettings,
            _fileService,
            _winetricksService,
            nodePath,
            _retroAchievementsGameIdentificationService,
            inherited,
            RootItems,
            parentNode,
            gogLink: dialogOwner => _gogWorkflow.LinkMediaItemToGogAsync(item, dialogOwner),
            epicLink: dialogOwner => _epicWorkflow.LinkMediaItemToEpicAsync(item, dialogOwner),
            gogInstallOrReinstall: dialogOwner =>
                _gogWorkflow.RunGogInstallFromEditorAsync(item, dialogOwner, requireAvailableUpdate: false),
            gogCheckUpdates: dialogOwner => _gogWorkflow.CheckGogUpdatesNowAsync(item, dialogOwner),
            gogUpdate: dialogOwner =>
                _gogWorkflow.RunGogInstallFromEditorAsync(item, dialogOwner, requireAvailableUpdate: true),
            gogUninstall: dialogOwner => _gogWorkflow.RunGogUninstallAsync(item, dialogOwner),
            gogManageDlcs: dialogOwner => _gogWorkflow.ShowGogDlcManagerAsync(item, dialogOwner),
            epicInstall: dialogOwner => _epicWorkflow.RunEpicInstallFromEditorAsync(item, dialogOwner),
            epicCheckUpdates: dialogOwner => _epicWorkflow.CheckEpicUpdatesNowAsync(item, dialogOwner),
            epicUpdate: dialogOwner => _epicWorkflow.RunEpicMaintenanceFromEditorAsync(item, dialogOwner, repair: false),
            epicRepair: dialogOwner => _epicWorkflow.RunEpicMaintenanceFromEditorAsync(item, dialogOwner, repair: true),
            epicUninstall: dialogOwner => _epicWorkflow.RunEpicUninstallAsync(item, dialogOwner),
            epicManageDlcs: dialogOwner => _epicWorkflow.ShowEpicDlcManagerAsync(item, dialogOwner),
            viewLastLaunchLog: dialogOwner => ShowLastLaunchLogAsync(item, dialogOwner),
            hasLastLaunchLog: _launchLogService.HasLog(item.Id))
        { 
            StorageProvider = StorageProvider ?? owner.StorageProvider 
        };
        
        var dialog = new EditMediaView { DataContext = editVm };
        
        // bool? so that "X" (no result) remains clearly identifiable
        var result = await dialog.ShowDialog<bool?>(owner);
        
        if (result == true)
        {
            _libraryTracker.MarkDirty();
            await SaveDataAndReleaseAssetsAsync(editVm.SavedAssetRemovals);

            if (parentNode != null)
                await RefreshItemPresentationAsync(parentNode);
        }
    }

    private async Task RefreshItemPresentationAsync(MediaNode parentNode)
    {
        if (_desktopContent.SearchArea is { } searchVm)
        {
            searchVm.RefreshResults();
            return;
        }

        var mediaVm = _desktopContent.MediaArea;
        var selectedNode = SelectedNode;
        if (mediaVm == null ||
            selectedNode == null ||
            !IsNodeInCurrentView(parentNode))
        {
            return;
        }

        var items = await DesktopMediaProjectionBuilder.CollectItemsAsync(
            selectedNode, IsParentalFilterActive, CancellationToken.None);

        await UiThreadHelper.InvokeAsync(() =>
        {
            if (ReferenceEquals(_desktopContent.MediaArea, mediaVm) &&
                ReferenceEquals(SelectedNode, selectedNode))
            {
                mediaVm.RefreshItems(items);
            }
        });
    }

    private async Task ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;

        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;

        var nodePath = PathHelper.GetNodePath(parentNode, RootItems);
        var manualImportSettings = new ScraperImportSettings
        {
            ExistingDataMode = ScraperExistingDataMode.OverwriteAlways,
            AppendAssetsDuringBulkScrape = true,
            ImportVideo = true,
            ImportManuals = true
        };

        var vm = new ScrapeDialogViewModel(
            item,
            _currentSettings,
            _metadataService,
            GameSystemResolver.ResolveForItem(item, parentNode, RootItems),
            item.GetPrimaryLaunchPath());
        
        vm.OnResultSelectedAsync += async (result) => 
        {
            var changed = false;

            // The manual dialog already contains an explicit per-field selection.
            // Selected metadata may therefore replace an existing value without
            // showing a second series of conflict prompts.
            if (ScrapedMetadataApplier.Apply(item, result, manualImportSettings))
                changed = true;

            var assets = await _scrapedAssetImporter.ImportAsync(
                    item,
                    result,
                    nodePath,
                    manualImportSettings,
                    appendAssetsWhenTypeExists: true);
            if (assets.Changed)
                changed = true;

            if (changed)
            {
                _libraryTracker.MarkDirty();
                await SaveData();
            }

            if (assets.DocumentImportError is { } documentError)
                throw new InvalidOperationException(string.Format(T("ScrapeDialog.DocumentImportFailed",
                    "{0}\nOther changes are retained. See the diagnostic log for details."), documentError));
            
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

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService, RootItems);
        var dialog = new BulkScrapeView { DataContext = vm };
        vm.OnBeforeStartAsync = async () =>
        {
            if (!_metadataBackups.ShouldCreateAutomaticBackup(MetadataBackupReason.BeforeBulkScrape))
                return true;

            try
            {
                await _metadataBackups.CreateBackupAsync(MetadataBackupReason.BeforeBulkScrape);
                return true;
            }
            catch (Exception ex)
            {
                var format = T(
                    "MetadataBackup.BeforeBulkScrapeFailedFormat",
                    "The bulk scrape was not started because the safety backup could not be created.\n\n{0}");
                if (!dialog.IsVisible)
                    return false;
                await ShowInfoDialog(dialog, string.Format(format, ex.Message));
                return false;
            }
        };
        vm.OnItemScrapedAsync = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return BulkScrapeImportOutcome.Failed;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
    
            var changed = false;
            var failedAssets = new List<AssetType>();

            if (ScrapedMetadataApplier.Apply(item, result, importSettings))
                changed = true;

            var assets = await _scrapedAssetImporter.ImportAsync(
                    item,
                    result,
                    nodePath,
                    importSettings,
                    appendAssetsWhenTypeExists: importSettings.AppendAssetsDuringBulkScrape,
                    onAssetImportFailed: failedAssets.Add);
            if (assets.Changed)
                changed = true;
            
            if (changed)
                _libraryTracker.MarkDirty();

            if (assets.DocumentImportError is { } documentError)
                throw new InvalidOperationException(string.Format(T("ScrapeDialog.DocumentImportFailed",
                    "{0}\nOther changes are retained. See the diagnostic log for details."), documentError));
            if (failedAssets.Count > 0)
                throw new InvalidOperationException(string.Format(T("BulkScrape.AssetImportFailed",
                    "At least one selected asset could not be imported: {0}. Other changes are retained."),
                    string.Join(", ", failedAssets)));
            return changed ? BulkScrapeImportOutcome.Imported : BulkScrapeImportOutcome.Skipped;
        };
    
        await dialog.ShowDialog(owner);
        // Closing cancels new work; an already running asset callback must finish before its changes are saved.
        if (vm.StartCommand.ExecutionTask is { } runningScrape)
            await runningScrape;
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private ScraperImportSettings GetScraperImportSettings()
    {
        _currentSettings.ScraperImport ??= new ScraperImportSettings();
        return _currentSettings.ScraperImport;
    }

    private EmulatorConfig? FindInheritedEmulator(MediaItem item)
    {
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode == null) return null;
        var nodeChain = PathHelper.GetNodeChain(parentNode, RootItems, matchById: true);
        nodeChain.Reverse();
        foreach (var node in nodeChain)
            if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                return _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
        return null;
    }

    private string? ResolveEffectiveDefaultEmulatorId(MediaNode targetNode)
    {
        var chain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true);
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

    private async Task RescanAllAssetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<MediaNode> rootNodes = new();
        await UiThreadHelper.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootNodes = RootItems.ToList();
        });

        var anyChanged = false;

        // Offload recursion and filesystem scanning to a background thread.
        await Task.Run(async () => 
        { 
            foreach (var rootNode in rootNodes) 
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await RescanNodeRecursive(rootNode, cancellationToken))
                    anyChanged = true;
            }
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (anyChanged)
            _libraryTracker.MarkDirty();
    }

    private async Task<bool> RescanNodeRecursive(
        MediaNode node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> nodePath = new();
        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        }, cancellationToken);

        var changed = false;

        // 2) Apply results on UI thread in batches to keep the UI responsive for very large nodes.
        const int batchSize = 500;
        for (int i = 0; i < scanned.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = i;
            var count = Math.Min(batchSize, scanned.Count - start);

            await UiThreadHelper.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

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
            cancellationToken.ThrowIfCancellationRequested();

            if (await RescanNodeRecursive(child, cancellationToken))
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
    
}
