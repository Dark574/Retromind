using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

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

            // Wenn nichts ausgewählt ist, starte beim letzten Element.
            if (SelectedItemIndex < 0) SelectedItemIndex = 0;
            
            // Kompakte Logik für "zurück mit Umbruch"
            SelectedItemIndex = (SelectedItemIndex - 1 + Items.Count) % Items.Count;

            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            // Wenn nichts ausgewählt ist, starte beim letzten Element.
            if (_selectedCategoryIndex < 0) _selectedCategoryIndex = 0;

            // Kompakte Logik für "zurück mit Umbruch"
            _selectedCategoryIndex = (_selectedCategoryIndex - 1 + CurrentCategories.Count) % CurrentCategories.Count;

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

            // Wenn nichts ausgewählt ist, starte beim ersten Element.
            if (SelectedItemIndex < 0) SelectedItemIndex = 0;
            
            // Kompakte Logik für "vorwärts mit Umbruch"
            SelectedItemIndex = (SelectedItemIndex + 1) % Items.Count;

            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            // Wenn nichts ausgewählt ist, starte beim ersten Element.
            if (_selectedCategoryIndex < 0) _selectedCategoryIndex = 0;

            // Kompakte Logik für "vorwärts mit Umbruch"
            _selectedCategoryIndex = (_selectedCategoryIndex + 1) % CurrentCategories.Count;

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
                
                // Theme-Kontext ist jetzt dieser Ordner
                ThemeContextNode = node;

                // Selection synchron setzen (kein fire-and-forget)
                SelectedCategory = CurrentCategories.FirstOrDefault();

                // Preview einheitlich über Debounce/Dispatcher
                TriggerPreviewPlaybackWithDebounce();
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

                // WICHTIG: Auch bei Leaf/Items ist der Theme-Kontext dieser Node!
                ThemeContextNode = node;
                
                SelectedItem = Items.FirstOrDefault();

                // Preview einheitlich
                TriggerPreviewPlaybackWithDebounce();
                return;
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
        TriggerPreviewPlaybackWithDebounce();
    }

    [RelayCommand]
    private void ExitBigMode()
    {
        // If we are currently in the game list, go back to category view first.
        if (IsGameListActive)
        {
            IsGameListActive = false;
            
            // Wheel-Theme zeigt Items immer an -> in Category-View leeren
            Items = new ObservableCollection<MediaItem>();
            SelectedItem = null;

            // Pop statt Peek, um Stack korrekt zu managen
            var previousList = _navigationStack.Count > 0 ? _navigationStack.Pop() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : "Main Menu";

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            // NavigationPath poppen, falls vorhanden
            if (_navigationPath.Count > 0) _navigationPath.Pop();
            
            // Theme-Kontext = aktueller Ordner (Peek) oder Root
            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;
            
            // Selection synchron setzen
            var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
            SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                ? leafNode
                : CurrentCategories.FirstOrDefault();

            // Preview einheitlich (vermeidet "Standbild" durch falsches Timing)
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
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : "Main Menu";
            if (_navigationPath.Count > 0) _navigationPath.Pop();

            CategoryTitle = previousTitle;
            CurrentCategories = previousList;

            // Theme-Kontext = aktueller Ordner (Peek) oder Root
            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : null;
            
            SelectedCategory = CurrentCategories.FirstOrDefault();

            TriggerPreviewPlaybackWithDebounce();
            return;
        }

        ThemeContextNode = null;
        RequestClose?.Invoke();
    }
}