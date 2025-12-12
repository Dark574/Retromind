using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    [RelayCommand]
    private void SelectPrevious()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            if (Items.Count == 0) return;
            if (SelectedItem == null) { SelectedItem = Items.Last(); return; }

            var index = Items.IndexOf(SelectedItem);
            if (index > 0) SelectedItem = Items[index - 1];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;
            if (SelectedCategory == null) { SelectedCategory = CurrentCategories.Last(); return; }

            var index = CurrentCategories.IndexOf(SelectedCategory);
            SelectedCategory = index > 0 ? CurrentCategories[index - 1] : CurrentCategories.Last();

            UpdateThemeForNode(SelectedCategory);
            PlayCategoryPreview(SelectedCategory);
        }
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            if (Items.Count == 0) return;
            if (SelectedItem == null) { SelectedItem = Items.First(); return; }

            var index = Items.IndexOf(SelectedItem);
            if (index < Items.Count - 1) SelectedItem = Items[index + 1];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;
            if (SelectedCategory == null) { SelectedCategory = CurrentCategories.First(); return; }

            var index = CurrentCategories.IndexOf(SelectedCategory);
            SelectedCategory = index < CurrentCategories.Count - 1 ? CurrentCategories[index + 1] : CurrentCategories.First();

            UpdateThemeForNode(SelectedCategory);
            PlayCategoryPreview(SelectedCategory);
        }
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (_isLaunching) return;

        // Category view
        if (!IsGameListActive)
        {
            if (SelectedCategory == null) return;

            var node = SelectedCategory;

            if (node.Children is { Count: > 0 })
            {
                _navigationStack.Push(_currentCategories);
                _titleStack.Push(CategoryTitle);
                _navigationPath.Push(node);

                CategoryTitle = node.Name;
                CurrentCategories = node.Children;

                SelectedCategory = CurrentCategories.FirstOrDefault();
                UpdateThemeForNode(SelectedCategory);
                PlayCategoryPreview(SelectedCategory);
                return;
            }

            if (node.Items is { Count: > 0 })
            {
                if (_navigationPath.Count == 0 || _navigationPath.Peek() != node)
                    _navigationPath.Push(node);

                CurrentNode = node;
                SelectedCategory = node;

                Items = node.Items;
                IsGameListActive = true;

                if (Items.Count > 0)
                {
                    SelectedItem = Items[0];
                    PlayPreview(SelectedItem);
                }
            }

            return;
        }

        // Game view
        if (SelectedItem == null) return;

        _isLaunching = true;
        StopVideo();
        _previewCts?.Cancel();

        if (RequestPlay != null)
            await RequestPlay(SelectedItem);

        _isLaunching = false;

        PlayPreview(SelectedItem);
    }

    [RelayCommand]
    private void ExitBigMode()
    {
        // If in game list -> go back to categories
        if (IsGameListActive)
        {
            IsGameListActive = false;
            StopVideo();

            var previousList = _navigationStack.Count > 0 ? _navigationStack.Peek() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Peek() : "Main Menu";

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
            SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                ? leafNode
                : CurrentCategories.FirstOrDefault();

            PlayCategoryPreview(SelectedCategory);

            _settings.LastBigModeSelectedNodeId = null;
            return;
        }

        // If already at root, exit
        if (CurrentCategories == _rootNodes)
        {
            RequestClose?.Invoke();
            return;
        }

        // Pop one navigation level
        if (_navigationStack.Count > 0)
        {
            var previousList = _navigationStack.Pop();
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : "Main Menu";
            if (_navigationPath.Count > 0) _navigationPath.Pop();

            CategoryTitle = previousTitle;
            CurrentCategories = previousList;

            SelectedCategory = CurrentCategories.FirstOrDefault();
            UpdateThemeForNode(SelectedCategory);
            PlayCategoryPreview(SelectedCategory);
            return;
        }

        RequestClose?.Invoke();
    }
}