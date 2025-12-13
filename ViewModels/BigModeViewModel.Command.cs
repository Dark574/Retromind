using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private void OnGamepadUp()
    {
        PlaySound(_theme.Sounds.Navigate);
        SelectPrevious();
    }
    
    private void OnGamepadDown()
    {
        PlaySound(_theme.Sounds.Navigate);
        SelectNext();
    }

    private void OnGamepadLeft()
    {
        PlaySound(_theme.Sounds.Navigate);
        SelectPrevious(); // Standardmäßig wie "Hoch"
    }

    private void OnGamepadRight()
    {
        PlaySound(_theme.Sounds.Navigate);
        SelectNext(); // Standardmäßig wie "Runter"
    }

    private async void OnGamepadSelect()
    {
        PlaySound(_theme.Sounds.Confirm);
        await PlayCurrent();
    }

    private void OnGamepadBack()
    {
        PlaySound(_theme.Sounds.Cancel);
        ExitBigMode();
    }
    
    // --- Sound-Helfermethode ---
    
    private void PlaySound(string? relativeSoundPath)
    {
        if (string.IsNullOrEmpty(relativeSoundPath)) return;
    
        // _theme.BasePath wird vom ThemeLoader gesetzt und ist der Pfad zum Theme-Verzeichnis
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

            if (_selectedItemIndex < 0)
                _selectedItemIndex = SelectedItem != null ? Items.IndexOf(SelectedItem) : Items.Count - 1;

            if (_selectedItemIndex < 0)
                _selectedItemIndex = Items.Count - 1;

            if (_selectedItemIndex > 0)
                _selectedItemIndex--;

            SelectedItem = Items[_selectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = SelectedCategory != null ? CurrentCategories.IndexOf(SelectedCategory) : CurrentCategories.Count - 1;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = CurrentCategories.Count - 1;

            _selectedCategoryIndex = _selectedCategoryIndex > 0 ? _selectedCategoryIndex - 1 : CurrentCategories.Count - 1;

            SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        }

        if (SelectedCategory == null && CurrentCategories.Any())
        {
            SelectedCategory = CurrentCategories.First();
        }
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
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = SelectedCategory != null ? CurrentCategories.IndexOf(SelectedCategory) : 0;

            if (_selectedCategoryIndex < 0)
                _selectedCategoryIndex = 0;

            _selectedCategoryIndex = _selectedCategoryIndex < CurrentCategories.Count - 1 ? _selectedCategoryIndex + 1 : 0;

            SelectedCategory = CurrentCategories[_selectedCategoryIndex];
        }
        
        // Nach Wechsel sicherstellen, dass erstes Item initial selected wird
        if (SelectedCategory == null && CurrentCategories.Any())
        {
            SelectedCategory = CurrentCategories.First();
        }
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
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);
                _navigationPath.Push(node);

                CategoryTitle = node.Name;
                CurrentCategories = node.Children;

                // KORRIGIERT: Expliziter Initial-Select mit Debug und Timing-Delay
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    Debug.WriteLine($"[BigMode] Before select in children of '{node.Name}'");
                    SelectedCategory = CurrentCategories.FirstOrDefault();
                    if (SelectedCategory != null)
                    {
                        Debug.WriteLine($"[BigMode] Entered children of '{node.Name}'. Selected first: '{SelectedCategory.Name}'");
                    }
                    await Task.Delay(50); // Gib Bindings Zeit zum Updaten
                });
                
                PlayCategoryPreview(SelectedCategory);
                return;
            }

            if (node.Items is { Count: > 0 })
            {
                // Push current state auch für GameList-Wechsel, um Ebene zu merken
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);
                
                // Ensure the leaf node is part of the navigation path for persistence/restore.
                if (_navigationPath.Count == 0 || _navigationPath.Peek() != node)
                    _navigationPath.Push(node);

                CurrentNode = node;
                SelectedCategory = node;

                Items = node.Items;
                IsGameListActive = true;

                if (Items.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        Debug.WriteLine("[BigMode] Before select in game list");
                        SelectedItem = Items[0];
                        Debug.WriteLine($"[BigMode] Switched to game list. Automatically selected first item: '{SelectedItem?.Title}'");
                        await Task.Delay(50); // Gib Bindings Zeit zum Updaten
                    });
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

            // Pop statt Peek, um Stack korrekt zu managen
            var previousList = _navigationStack.Count > 0 ? _navigationStack.Pop() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : "Main Menu";

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            // NavigationPath poppen, falls vorhanden
            if (_navigationPath.Count > 0) _navigationPath.Pop();
            
            // KORRIGIERT: Expliziter Initial-Select nach Rückkehr mit Debug und Timing-Delay
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Debug.WriteLine("[BigMode] Before select after exiting game list");
                var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
                SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                    ? leafNode
                    : CurrentCategories.FirstOrDefault();

                if (SelectedCategory != null)
                {
                    Debug.WriteLine($"[BigMode] Exited game list. Selected: '{SelectedCategory.Name}'");
                }
                await Task.Delay(50); // Gib Bindings Zeit zum Updaten
            });
            
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

            // Expliziter Initial-Select nach Pop mit Debug
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Debug.WriteLine("[BigMode] Before select after pop");
                SelectedCategory = CurrentCategories.FirstOrDefault();
                if (SelectedCategory != null)
                {
                    Debug.WriteLine($"[BigMode] Popped navigation level. Selected first: '{SelectedCategory.Name}'");
                }
        
                // Nach Pop sicherstellen, dass erstes Item initial selected wird
                if (SelectedCategory == null && CurrentCategories.Any())
                {
                    SelectedCategory = CurrentCategories.First();
                }
                await Task.Delay(50); // Gib Bindings Zeit zum Updaten
            });
            
            PlayCategoryPreview(SelectedCategory);
            return;
        }

        // Safety fallback: if stacks are empty but we are not at root, still exit.
        RequestClose?.Invoke();
    }
}