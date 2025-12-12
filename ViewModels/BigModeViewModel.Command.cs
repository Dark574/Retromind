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

            if (_selectedItemIndex < 0)
                _selectedItemIndex = SelectedItem != null ? Items.IndexOf(SelectedItem) : Items.Count - 1;

            if (_selectedItemIndex < 0)
                _selectedItemIndex = Items.Count - 1;

            if (_selectedItemIndex > 0)
                _selectedItemIndex--;

            SelectedItem = Items[_selectedItemIndex];
            return;
        }

        if (CurrentCategories.Count == 0) return;

        if (_selectedCategoryIndex < 0)
            _selectedCategoryIndex = SelectedCategory != null ? CurrentCategories.IndexOf(SelectedCategory) : CurrentCategories.Count - 1;

        if (_selectedCategoryIndex < 0)
            _selectedCategoryIndex = CurrentCategories.Count - 1;

        _selectedCategoryIndex = _selectedCategoryIndex > 0 ? _selectedCategoryIndex - 1 : CurrentCategories.Count - 1;

        SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        UpdateThemeForNode(SelectedCategory);
        PlayCategoryPreview(SelectedCategory);
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            if (Items.Count == 0) return;

            if (_selectedItemIndex < 0)
                _selectedItemIndex = SelectedItem != null ? Items.IndexOf(SelectedItem) : 0;

            if (_selectedItemIndex < 0)
                _selectedItemIndex = 0;

            if (_selectedItemIndex < Items.Count - 1)
                _selectedItemIndex++;

            SelectedItem = Items[_selectedItemIndex];
            return;
        }

        if (CurrentCategories.Count == 0) return;

        if (_selectedCategoryIndex < 0)
            _selectedCategoryIndex = SelectedCategory != null ? CurrentCategories.IndexOf(SelectedCategory) : 0;

        if (_selectedCategoryIndex < 0)
            _selectedCategoryIndex = 0;

        _selectedCategoryIndex = _selectedCategoryIndex < CurrentCategories.Count - 1 ? _selectedCategoryIndex + 1 : 0;

        SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        UpdateThemeForNode(SelectedCategory);
        PlayCategoryPreview(SelectedCategory);
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (_isLaunching) return;

        // Category view: Enter folder (children) OR switch into game list (items).
        if (!IsGameListActive)
        {
            if (SelectedCategory == null) return;

            var node = SelectedCategory;

            if (node.Children is { Count: > 0 })
            {
                // Push current state so ExitBigMode can navigate back.
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
                // Ensure the leaf node is part of the navigation path for persistence/restore.
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

        // Game view: Launch the selected game.
        if (SelectedItem == null) return;

        _isLaunching = true;
        StopVideo();
        _previewCts?.Cancel();

        if (RequestPlay != null)
        {
            await RequestPlay(SelectedItem);
        }

        _isLaunching = false;

        // Resume preview once the game returns.
        PlayPreview(SelectedItem);
    }

    [RelayCommand]
    private void ExitBigMode()
    {
        // If we are currently in the game list, go back to category view first.
        if (IsGameListActive)
        {
            IsGameListActive = false;
            StopVideo();

            // After restore, CurrentCategories may point to a leaf's Children (empty).
            // Using the navigation stack ensures we return to the correct category level.
            var previousList = _navigationStack.Count > 0 ? _navigationStack.Peek() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Peek() : "Main Menu";

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
            SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                ? leafNode
                : CurrentCategories.FirstOrDefault();

            PlayCategoryPreview(SelectedCategory);

            // Clear item selection persistence when leaving the game list explicitly.
            _settings.LastBigModeSelectedNodeId = null;
            return;
        }

        // If we are already at root, exit BigMode completely.
        if (CurrentCategories == _rootNodes)
        {
            RequestClose?.Invoke();
            return;
        }

        // Pop one navigation level.
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

        // Safety fallback: if stacks are empty but we are not at root, still exit.
        RequestClose?.Invoke();
    }
}