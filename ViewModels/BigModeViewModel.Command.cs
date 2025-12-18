using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private void OnGamepadUp()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Navigate);
            SelectPrevious();
        });

    private void OnGamepadDown()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Navigate);
            SelectNext();
        });

    private void OnGamepadLeft()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Navigate);
            // Default mapping: left behaves like "previous".
            SelectPrevious();
        });

    private void OnGamepadRight()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Navigate);
            // Default mapping: right behaves like "next".
            SelectNext();
        });

    private void OnGamepadSelect()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Confirm);
            _ = PlayCurrent();
        });

    private void OnGamepadBack()
        => DispatchGamepadAction(() =>
        {
            PlaySound(_theme.Sounds.Cancel);
            ExitBigMode();
        });

    /// <summary>
    /// Gamepad callbacks arrive on the SDL thread.
    /// All UI-bound state changes must be marshaled to the UI thread.
    /// </summary>
    private static void DispatchGamepadAction(System.Action action)
    {
        if (action == null) return;

        // Gamepad callbacks arrive on the SDL thread.
        // All UI-bound state changes must be marshaled to the UI thread.
        UiThreadHelper.Post(action, DispatcherPriority.Input);
    }

    private void PlaySound(string? relativeSoundPath)
    {
        if (string.IsNullOrWhiteSpace(relativeSoundPath))
            return;

        // Theme.BasePath is the theme directory as provided by ThemeLoader.
        var fullPath = Path.Combine(_theme.BasePath, relativeSoundPath);
        _soundEffectService.PlaySound(fullPath);
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            if (Items.Count == 0) return;

            if (SelectedItemIndex < 0)
                SelectedItemIndex = 0;

            // Wrap-around navigation.
            SelectedItemIndex = (SelectedItemIndex - 1 + Items.Count) % Items.Count;
            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = 0;

            // Wrap-around navigation.
            _selectedCategoryIndex = (_selectedCategoryIndex - 1 + CurrentCategories.Count) % CurrentCategories.Count;
            SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        }

        // Defensive fallback (should rarely be needed).
        if (SelectedCategory == null && CurrentCategories.Any())
            SelectedCategory = CurrentCategories.First();
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            if (Items.Count == 0) return;

            if (SelectedItemIndex < 0)
                SelectedItemIndex = 0;

            // Wrap-around navigation.
            SelectedItemIndex = (SelectedItemIndex + 1) % Items.Count;
            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = 0;

            // Wrap-around navigation.
            _selectedCategoryIndex = (_selectedCategoryIndex + 1) % CurrentCategories.Count;
            SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        }

        // Defensive fallback (should rarely be needed).
        if (SelectedCategory == null && CurrentCategories.Any())
            SelectedCategory = CurrentCategories.First();
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (_isLaunching) return;

        // Category view: enter folder (children) or switch into game list (items).
        if (!IsGameListActive)
        {
            if (SelectedCategory == null) return;

            var node = SelectedCategory;

            if (node.Children is { Count: > 0 })
            {
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);
                _navigationPath.Push(node);

                CategoryTitle = node.Name;
                CurrentCategories = node.Children;

                // Theme context becomes the current node.
                ThemeContextNode = node;

                SelectedCategory = CurrentCategories.FirstOrDefault();

                TriggerPreviewPlaybackWithDebounce();
                return;
            }

            if (node.Items is { Count: > 0 })
            {
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);

                // Ensure leaf is part of the navigation path for persistence/restore.
                if (_navigationPath.Count == 0 || _navigationPath.Peek() != node)
                    _navigationPath.Push(node);

                CurrentNode = node;
                SelectedCategory = node;

                Items = node.Items;
                IsGameListActive = true;

                // Theme context is also the leaf node.
                ThemeContextNode = node;

                SelectedItem = Items.FirstOrDefault();

                TriggerPreviewPlaybackWithDebounce();
                return;
            }

            return;
        }

        // Game view: launch the selected item.
        if (SelectedItem == null) return;

        _isLaunching = true;

        StopVideo();
        _previewDebounceCts?.Cancel();

        if (RequestPlay != null)
            await RequestPlay(SelectedItem);

        _isLaunching = false;

        // Resume preview once the game returns.
        TriggerPreviewPlaybackWithDebounce();
    }

    [RelayCommand]
    private void ExitBigMode()
    {
        // If we are currently in the game list, go back to category view first.
        if (IsGameListActive)
        {
            IsGameListActive = false;

            // Clear item list in category view.
            Items = new ObservableCollection<MediaItem>();
            SelectedItem = null;

            var previousList = _navigationStack.Count > 0 ? _navigationStack.Pop() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : Strings.BigMode_MainMenu;

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            if (_navigationPath.Count > 0) _navigationPath.Pop();

            // Theme context = current folder (peek) or root.
            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;

            // Restore selection to the leaf node if it exists in the current level.
            var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
            SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                ? leafNode
                : CurrentCategories.FirstOrDefault();

            TriggerPreviewPlaybackWithDebounce();

            _settings.LastBigModeSelectedNodeId = null;
            return;
        }

        // If we are already at root, exit BigMode completely.
        if (CurrentCategories == _rootNodes)
        {
            ThemeContextNode = null;
            RequestClose?.Invoke();
            return;
        }

        // Pop one navigation level.
        if (_navigationStack.Count > 0)
        {
            var previousList = _navigationStack.Pop();
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : Strings.BigMode_MainMenu;

            if (_navigationPath.Count > 0) _navigationPath.Pop();

            CategoryTitle = previousTitle;
            CurrentCategories = previousList;

            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;
            SelectedCategory = CurrentCategories.FirstOrDefault();

            TriggerPreviewPlaybackWithDebounce();
            return;
        }

        ThemeContextNode = null;
        RequestClose?.Invoke();
    }
}