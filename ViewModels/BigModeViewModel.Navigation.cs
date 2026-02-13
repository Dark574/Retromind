using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    /// <summary>
    /// Restores BigMode state from <see cref="AppSettings"/>.
    /// This is intentionally defensive because the library structure can change (deleted/moved nodes),
    /// and settings may come from older versions or manual edits.
    /// </summary>
    private void RestoreLastState()
    {
        try
        {
            // 1) Validate the persisted navigation path. If it no longer matches the current tree, discard it.
            if (_settings.LastBigModeNavigationPath is { Count: > 0 } path &&
                !IsNavigationPathValid(path))
            {
                _settings.LastBigModeNavigationPath = null;
                _settings.LastBigModeSelectedNodeId = null;
                _settings.LastBigModeWasItemView = false;
            }

            // 2) If BigMode state is missing, derive a sensible start state from the CoreApp selection.
            if ((_settings.LastBigModeNavigationPath == null || _settings.LastBigModeNavigationPath.Count == 0) &&
                !string.IsNullOrWhiteSpace(_settings.LastSelectedNodeId))
            {
                if (TryBuildNavigationPathFromNodeId(_settings.LastSelectedNodeId!, out var derivedPath))
                {
                    _settings.LastBigModeNavigationPath = derivedPath;

                    if (!string.IsNullOrWhiteSpace(_settings.LastSelectedMediaId))
                    {
                        _settings.LastBigModeWasItemView = true;
                        _settings.LastBigModeSelectedNodeId = _settings.LastSelectedMediaId;
                    }
                    else
                    {
                        _settings.LastBigModeWasItemView = false;
                        _settings.LastBigModeSelectedNodeId = _settings.LastSelectedNodeId;
                    }
                }
                else
                {
                    // Neither BigMode state nor CoreApp selection can be resolved -> reset to a consistent root state.
                    _settings.LastSelectedNodeId = null;
                    _settings.LastSelectedMediaId = null;
                    _settings.LastBigModeNavigationPath = null;
                    _settings.LastBigModeSelectedNodeId = null;
                    _settings.LastBigModeWasItemView = false;

                    ResetToRootState();
                    return;
                }
            }

            // 3) Rebuild navigation stacks from the persisted path.
            var currentLevel = _rootNodes;

            if (_settings.LastBigModeNavigationPath != null)
            {
                foreach (var nodeId in _settings.LastBigModeNavigationPath)
                {
                    var nodeToEnter = currentLevel.FirstOrDefault(n => n.Id == nodeId);
                    if (nodeToEnter == null)
                        throw new Exception($"Node '{nodeId}' not found in navigation path.");

                    _navigationStack.Push(currentLevel);
                    _titleStack.Push(CategoryTitle);
                    _navigationPath.Push(nodeToEnter);

                    CategoryTitle = nodeToEnter.Name;
                    currentLevel = nodeToEnter.Children;
                }
            }

            CurrentCategories = currentLevel;

            // 4) Restore view mode (categories vs. games) and selection.
            if (_settings.LastBigModeWasItemView)
            {
                var parentNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;
                // Only switch to the item view if there are actually items
                if (parentNode != null && parentNode.Items is { Count: > 0 })
                {
                    IsGameListActive = true;
                    Items = parentNode.Items;
                    SelectedCategory = parentNode;

                    // Ensure ThemeContextNode is set in item view too, to trigger preview consistently.
                    ThemeContextNode = parentNode;
                    
                    var item = parentNode.Items.FirstOrDefault(i => i.Id == _settings.LastBigModeSelectedNodeId);
                    SelectedItem = item ?? Items.FirstOrDefault();

                    return;
                }
                
                // Fallback: If the saved item view is no longer relevant
                // (e.g., because the node has no items (anymore) or was not found),
                // revert to the category view
                _settings.LastBigModeWasItemView = false;
                _settings.LastBigModeSelectedNodeId = null;
            }
            
            // Restore category view
            // Try to find the last selected category on the current level
            MediaNode? category = null;
            if (!string.IsNullOrWhiteSpace(_settings.LastBigModeSelectedNodeId))
            {
                category = CurrentCategories.FirstOrDefault(c => c.Id == _settings.LastBigModeSelectedNodeId);
            }

            if (category != null)
            {
                SelectedCategory = category;
            }
            else if (CurrentCategories.Count > 0)
            {
                // Fallback: Select the first category so that a valid selection ALWAYS exists
                SelectedCategory = CurrentCategories[0];
            }
            else
            {
                SelectedCategory = null;
            }

            // ThemeContextNode = current folder (topmost entry in the NavigationPath) or selected root
            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : SelectedCategory;

            // Make sure we are in category mode
            IsGameListActive = false;
            Items = new ObservableCollection<MediaItem>();
            SelectedItem = null;
        }
        catch (Exception ex)
        {
            // Restore must never break BigMode startup.
            System.Diagnostics.Debug.WriteLine($"[BigMode] Restore state failed: {ex.Message}");
            ResetToRootState();
        }
    }

    /// <summary>
    /// Resets the whole BigMode navigation/view state to a consistent root menu.
    /// </summary>
    private void ResetToRootState()
    {
        _navigationStack.Clear();
        _titleStack.Clear();
        _navigationPath.Clear();

        StopVideo();

        // Reset caches (safe + avoids stale paths if the library changes)
        _itemVideoPathCache.Clear();
        _nodeVideoPathCache.Clear();

        IsGameListActive = false;
        Items = new ObservableCollection<MediaItem>();

        CategoryTitle = Strings.BigMode_MainMenu;
        CurrentCategories = _rootNodes;
        SelectedCategory = _rootNodes.FirstOrDefault();
        SelectedItem = null;
        CurrentNode = SelectedCategory;

        // Root context: use the selected root node for theme selection.
        ThemeContextNode = SelectedCategory;
        
        // No active game list at root -> reset counters explicitly.
        UpdateGameCounters();
    }

    /// <summary>
    /// Validates that the given path can be traversed in the current tree structure.
    /// </summary>
    private bool IsNavigationPathValid(IReadOnlyList<string> pathIds)
    {
        IEnumerable<MediaNode> currentLevel = _rootNodes;

        foreach (var nodeId in pathIds)
        {
            var node = currentLevel.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;

            currentLevel = node.Children;
        }

        return true;
    }

    partial void OnCurrentCategoriesChanged(ObservableCollection<MediaNode> value)
    {
        SelectedCategoryIndex = -1;
    }

    partial void OnItemsChanging(ObservableCollection<MediaItem> value)
    {
        _lastItemsForNodeFallback = Items;
    }

    partial void OnItemsChanged(ObservableCollection<MediaItem> value)
    {
        SelectedItemIndex = -1;

        // Items list changed -> cached item video paths may no longer be relevant.
        _itemVideoPathCache.Clear();

        if (_lastItemsForNodeFallback != null && !ReferenceEquals(_lastItemsForNodeFallback, value))
            ClearNodeFallbackOverrides(_lastItemsForNodeFallback);

        _lastItemsForNodeFallback = value;
        ApplyNodeFallbackOverrides();
        
        // Keep game counters in sync with the new item collection.
        UpdateGameCounters();
        UpdateCircularItems();
    }


    /// <summary>
    /// Builds a root-to-node path for a target node id (used for CoreApp -> BigMode fallback).
    /// </summary>
    private bool TryBuildNavigationPathFromNodeId(string nodeId, out List<string> path)
    {
        path = new List<string>();
        return TryFindPathRecursive(_rootNodes, nodeId, path);
    }

    private static bool TryFindPathRecursive(IEnumerable<MediaNode> level, string nodeId, List<string> path)
    {
        foreach (var node in level)
        {
            path.Add(node.Id);

            if (node.Id == nodeId)
                return true;

            if (node.Children is { Count: > 0 } && TryFindPathRecursive(node.Children, nodeId, path))
                return true;

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    /// <summary>
    /// Persists BigMode navigation state and mirrors the most relevant selection into CoreApp settings.
    /// The method is intentionally idempotent (it may be called from multiple shutdown paths).
    /// </summary>
    public void SaveState()
    {
        if (_stateSaved) return;
        _stateSaved = true;

        _settings.LastBigModeNavigationPath = _navigationPath.Reverse().Select(n => n.Id).ToList();

        // Some themes (e.g. Arcade/Wheel) can end up showing items even if IsGameListActive is false.
        // Treat "item view" as active when we still have a valid selected item and a populated items list.
        var isItemView = IsGameListActive || (SelectedItem != null && Items.Count > 0);
        _settings.LastBigModeWasItemView = isItemView;

        var nodeForState = SelectedCategory
            ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null)
            ?? ThemeContextNode
            ?? CurrentNode;

        if (isItemView)
        {
            _settings.LastBigModeSelectedNodeId = SelectedItem?.Id;

            // Mirror to CoreApp
            _settings.LastSelectedNodeId = nodeForState?.Id;
            _settings.LastSelectedMediaId = SelectedItem?.Id;
        }
        else
        {
            _settings.LastBigModeSelectedNodeId = nodeForState?.Id;

            // Mirror to CoreApp
            _settings.LastSelectedNodeId = nodeForState?.Id;
            _settings.LastSelectedMediaId = null;
        }

    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        ClearNodeFallbackOverrides(Items);

        _gamepadService.OnDirectionStateChanged -= OnGamepadDirectionStateChanged;
        _gamepadService.OnSelect -= OnGamepadSelect;
        _gamepadService.OnBack -= OnGamepadBack;

        _videoSurfaceA.FrameReady -= OnMainVideoFrameReadyA;
        _videoSurfaceB.FrameReady -= OnMainVideoFrameReadyB;
        DisposeGamepadRepeatTimer();

        // Stop attract timer early so it can't fire while we tear down VLC
        try
        {
            if (_attractTimer != null)
            {
                _attractTimer.Tick -= OnAttractTimerTick;
                _attractTimer.Stop();
                _attractTimer.IsEnabled = false;
                _attractTimer = null;
            }
        }
        catch
        {
            // best effort
        }

        // Stop preview debounce timer (new UI-thread debounce, replaces the old CTS/Task.Run approach)
        try
        {
            DisposePreviewDebounceTimer();
        }
        catch
        {
            // ignore
        }

        var mainPlayerA = _mediaPlayerA;
        var mainPlayerB = _mediaPlayerB;
        var secondaryPlayer = _secondaryPlayer;
        var vlc = _libVlc;
        var secondaryVlc = _secondaryLibVlc;

        MediaPlayer = null;
        _mediaPlayerA = null;
        _mediaPlayerB = null;
        _secondaryPlayer = null;

        SaveState();

        _ = Task.Run(() =>
        {
            try
            {
                // Stop & dispose secondary channel first
                try
                {
                    if (secondaryPlayer != null)
                    {
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        secondaryPlayer.Stop();
                        secondaryPlayer.Media = null;
                        secondaryPlayer.Dispose();
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _secondaryBackgroundMedia?.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _secondaryBackgroundMedia = null;
                }

                // Stop & dispose main channel
                try
                {
                    if (mainPlayerA != null)
                    {
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        mainPlayerA.Stop();
                        mainPlayerA.Media = null;
                        mainPlayerA.Dispose();
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    if (mainPlayerB != null)
                    {
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        mainPlayerB.Stop();
                        mainPlayerB.Media = null;
                        mainPlayerB.Dispose();
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _currentPreviewMediaA?.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _currentPreviewMediaA = null;
                }

                try
                {
                    _currentPreviewMediaB?.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _currentPreviewMediaB = null;
                }

                // Dispose LibVLC last
                try
                {
                    vlc.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    secondaryVlc.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _videoSurfaceA.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _videoSurfaceB.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _secondaryVideoSurface?.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // Best-effort cleanup; shutdown should not crash because VLC cleanup failed.
            }
        });
    }
}
