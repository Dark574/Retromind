using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    /// <summary>
    /// Restores BigMode state. Includes sanity checks and fallback to CoreApp selection.
    /// This method is intentionally defensive: users can delete/move nodes or edit settings manually.
    /// </summary>
    private void RestoreLastState()
    {
        SelectedCategory = CurrentCategories.FirstOrDefault();

        try
        {
            // 1) Validate persisted BigMode path
            if (_settings.LastBigModeNavigationPath is { Count: > 0 } path &&
                !IsNavigationPathValid(path))
            {
                _settings.LastBigModeNavigationPath = null;
                _settings.LastBigModeSelectedNodeId = null;
                _settings.LastBigModeWasItemView = false;
            }

            // 2) Fallback: derive BigMode state from CoreApp selection
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
                    // Both BigMode and CoreApp selection are invalid -> reset to a consistent root state.
                    _settings.LastSelectedNodeId = null;
                    _settings.LastSelectedMediaId = null;
                    _settings.LastBigModeNavigationPath = null;
                    _settings.LastBigModeSelectedNodeId = null;
                    _settings.LastBigModeWasItemView = false;

                    ResetToRootState();
                    return;
                }
            }

            // 3) Rebuild navigation stacks
            var currentLevel = _rootNodes;

            if (_settings.LastBigModeNavigationPath != null)
            {
                foreach (var nodeId in _settings.LastBigModeNavigationPath)
                {
                    var nodeToEnter = currentLevel.FirstOrDefault(n => n.Id == nodeId);
                    if (nodeToEnter == null) throw new Exception($"Node '{nodeId}' not found in path.");

                    _navigationStack.Push(currentLevel);
                    _titleStack.Push(CategoryTitle);
                    _navigationPath.Push(nodeToEnter);

                    CategoryTitle = nodeToEnter.Name;
                    currentLevel = nodeToEnter.Children;
                }
            }

            CurrentCategories = currentLevel;

            // 4) Restore mode and selection
            if (_settings.LastBigModeWasItemView)
            {
                var parentNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;
                if (parentNode != null)
                {
                    UpdateThemeForNode(parentNode);

                    IsGameListActive = true;
                    Items = parentNode.Items;
                    SelectedCategory = parentNode;

                    var item = parentNode.Items.FirstOrDefault(i => i.Id == _settings.LastBigModeSelectedNodeId);
                    SelectedItem = item ?? Items.FirstOrDefault();

                    return;
                }
            }
            else
            {
                var category = CurrentCategories.FirstOrDefault(c => c.Id == _settings.LastBigModeSelectedNodeId);
                if (category != null) SelectedCategory = category;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BigMode] Restore state failed: {ex.Message}");
            ResetToRootState();
            return;
        }

        UpdateThemeForNode(SelectedCategory);
    }

    /// <summary>
    /// Resets the entire BigMode UI state to a consistent root state.
    /// </summary>
    private void ResetToRootState()
    {
        _navigationStack.Clear();
        _titleStack.Clear();
        _navigationPath.Clear();

        _previewCts?.Cancel();
        StopVideo();

        IsGameListActive = false;
        Items = new ObservableCollection<MediaItem>();

        CategoryTitle = "Main Menu";
        CurrentCategories = _rootNodes;
        SelectedCategory = _rootNodes.FirstOrDefault();
        SelectedItem = null;
        CurrentNode = SelectedCategory;

        UpdateThemeForNode(SelectedCategory);
    }

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

    private bool TryBuildNavigationPathFromNodeId(string nodeId, out List<string> path)
    {
        path = new List<string>();
        return TryFindPathRecursive(_rootNodes, nodeId, path);
    }

    private bool TryFindPathRecursive(IEnumerable<MediaNode> level, string nodeId, List<string> path)
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

    public void SaveState()
    {
        if (_stateSaved) return;
        _stateSaved = true;

        _settings.LastBigModeNavigationPath = _navigationPath.Reverse().Select(n => n.Id).ToList();
        _settings.LastBigModeWasItemView = IsGameListActive;

        if (IsGameListActive)
        {
            _settings.LastBigModeSelectedNodeId = SelectedItem?.Id;

            // Mirror to CoreApp
            _settings.LastSelectedNodeId = SelectedCategory?.Id;
            _settings.LastSelectedMediaId = SelectedItem?.Id;
        }
        else
        {
            _settings.LastBigModeSelectedNodeId = SelectedCategory?.Id;

            // Mirror to CoreApp
            _settings.LastSelectedNodeId = SelectedCategory?.Id;
            _settings.LastSelectedMediaId = null;
        }
    }

    public void Dispose()
    {
        var player = MediaPlayer;
        var vlc = _libVlc;

        MediaPlayer = null;

        SaveState();

        _ = Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                vlc?.Dispose();
            }
            catch
            {
                // Best-effort cleanup; we don't want shutdown crashes here.
            }
        });
    }
}