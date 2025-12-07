using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // --- Commands ---
    public ICommand AddCategoryCommand { get; private set; } = null!;
    public ICommand AddMediaCommand { get; private set; } = null!;
    public ICommand DeleteCommand { get; private set; } = null!;
    public ICommand SetCoverCommand { get; private set; } = null!;
    public ICommand SetLogoCommand { get; private set; } = null!;
    public ICommand SetWallpaperCommand { get; private set; } = null!;
    public ICommand SetMusicCommand { get; private set; } = null!;
    public ICommand EditMediaCommand { get; private set; } = null!;
    public ICommand DeleteMediaCommand { get; private set; } = null!;
    public ICommand PlayCommand { get; private set; } = null!;
    public ICommand OpenSettingsCommand { get; private set; } = null!;
    public ICommand EditNodeCommand { get; private set; } = null!;
    public ICommand ToggleThemeCommand { get; private set; } = null!;
    public ICommand ImportRomsCommand { get; private set; } = null!;
    public ICommand ImportSteamCommand { get; private set; } = null!;
    public ICommand ImportGogCommand { get; private set; } = null!;
    public ICommand ScrapeMediaCommand { get; private set; } = null!;
    public ICommand ScrapeNodeCommand { get; private set; } = null!;
    public ICommand OpenSearchCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        AddCategoryCommand = new RelayCommand<MediaNode?>(AddCategoryAsync);
        AddMediaCommand = new RelayCommand<MediaNode?>(AddMediaAsync);
        DeleteCommand = new RelayCommand<MediaNode?>(DeleteNodeAsync);
        SetCoverCommand = new RelayCommand<MediaItem?>(SetCoverAsync);
        SetLogoCommand = new RelayCommand<MediaItem?>(SetLogoAsync);
        SetWallpaperCommand = new RelayCommand<MediaItem?>(SetWallpaperAsync);
        SetMusicCommand = new RelayCommand<MediaItem?>(SetMusicAsync);
        EditMediaCommand = new RelayCommand<MediaItem?>(EditMediaAsync);
        DeleteMediaCommand = new RelayCommand<MediaItem?>(DeleteMediaAsync);
        PlayCommand = new RelayCommand<MediaItem?>(PlayMedia);
        OpenSettingsCommand = new RelayCommand(OpenSettingsAsync);
        EditNodeCommand = new RelayCommand<MediaNode?>(EditNodeAsync);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ImportRomsCommand = new RelayCommand<MediaNode?>(ImportRomsAsync);
        ImportSteamCommand = new RelayCommand<MediaNode?>(ImportSteamAsync);
        ImportGogCommand = new RelayCommand<MediaNode?>(ImportGogAsync);
        ScrapeMediaCommand = new RelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new RelayCommand<MediaNode?>(ScrapeNodeAsync);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
    }

    // --- Basic Actions ---

    private async void AddCategoryAsync(MediaNode? parentNode)
    {
        if (CurrentWindow is not { } owner) return;
        var name = await PromptForName(owner, Strings.Dialog_EnterName_Message);
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (parentNode == null) RootItems.Add(new MediaNode(name, NodeType.Area));
            else
            {
                parentNode.Children.Add(new MediaNode(name, NodeType.Group));
                parentNode.IsExpanded = true; 
            }
            await SaveData();
        }
    }

    private async void DeleteNodeAsync(MediaNode? nodeToDelete)
    {
        if (nodeToDelete == null || CurrentWindow is not { } owner) return;
        if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete)) return;

        if (RootItems.Contains(nodeToDelete)) RootItems.Remove(nodeToDelete);
        else RemoveNodeRecursive(RootItems, nodeToDelete);
        await SaveData();
    }

    private async void EditNodeAsync(MediaNode? node)
    {
        if (node == null || CurrentWindow is not { } owner) return;
        var vm = new NodeSettingsViewModel(node, _currentSettings);
        var dialog = new NodeSettingsView { DataContext = vm };
        vm.RequestClose += saved => { dialog.Close(); };
        await dialog.ShowDialog(owner);
        await SaveData();
    }

    private async void PlayMedia(MediaItem? item)
    {
        if (item == null || SelectedNode == null) return;
        _audioService.StopMusic();

        EmulatorConfig? emulator = null;
        if (!string.IsNullOrEmpty(item.EmulatorId))
            emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);

        var trueParent = FindParentNode(RootItems, item) ?? SelectedNode;
        var nodePath = PathHelper.GetNodePath(trueParent, RootItems);

        if (emulator == null)
        {
            var nodeChain = GetNodeChain(trueParent, RootItems);
            nodeChain.Reverse(); 
            foreach (var node in nodeChain)
            {
                if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                {
                    emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
                    if (emulator != null) break;
                }
            }
        }
        await _launcherService.LaunchAsync(item, emulator, nodePath);
        if (SelectedNodeContent is MediaAreaViewModel vm && vm.SelectedMediaItem == item && !string.IsNullOrEmpty(item.MusicPath))
        {
            _ = _audioService.PlayMusicAsync(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath));
        }
        await SaveData();
    }

    private async void DeleteMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete)) return;

        if (item == (SelectedNodeContent as MediaAreaViewModel)?.SelectedMediaItem) _audioService.StopMusic();
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode != null)
        {
            parentNode.Items.Remove(item);
            await SaveData();
            UpdateContent();
        }
    }

    // --- Dialog Helpers ---

    private async Task<bool> ShowConfirmDialog(Window owner, string message)
    {
        var dialog = new ConfirmView { DataContext = message };
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task<string?> PromptForName(Window owner, string message)
    {
        var dialog = new NamePromptView { DataContext = new NamePromptViewModel(message, message) };
        var result = await dialog.ShowDialog<bool>(owner);
        return result && dialog.DataContext is NamePromptViewModel vm ? vm.InputText : null;
    }
    
    private async void OpenSettingsAsync()
    {
        if (CurrentWindow is not { } owner) return;

        var settingsVm = new SettingsViewModel(_currentSettings);
        var dialog = new SettingsView
        {
            DataContext = settingsVm
        };

        settingsVm.RequestClose += () => { dialog.Close(); };
    
        await dialog.ShowDialog(owner);
        SaveSettingsOnly();
    }

    // --- Tree Helpers ---

    private void CollectItemsRecursive(MediaNode node, List<MediaItem> targetList)
    {
        targetList.AddRange(node.Items);
        foreach (var child in node.Children) CollectItemsRecursive(child, targetList);
    }

    private void SortAllNodesRecursive(IEnumerable<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            SortMediaItems(node.Items);
            SortAllNodesRecursive(node.Children);
        }
    }

    private void SortMediaItems(ObservableCollection<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.Title).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (items.IndexOf(sorted[i]) != i) items.Move(items.IndexOf(sorted[i]), i);
        }
    }

    private bool RemoveNodeRecursive(ObservableCollection<MediaNode> nodes, MediaNode nodeToDelete)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Remove(nodeToDelete)) return true;
            if (RemoveNodeRecursive(node.Children, nodeToDelete)) return true;
        }
        return false;
    }

    private MediaNode? FindNodeById(IEnumerable<MediaNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNodeById(node.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    private bool ExpandPathToNode(IEnumerable<MediaNode> nodes, MediaNode target)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (ExpandPathToNode(node.Children, target)) { node.IsExpanded = true; return true; }
        }
        return false;
    }
    
    private MediaNode? FindParentNode(IEnumerable<MediaNode> nodes, MediaItem item)
    {
        foreach (var node in nodes)
        {
            if (node.Items.Contains(item)) return node;
            var found = FindParentNode(node.Children, item);
            if (found != null) return found;
        }
        return null;
    }

    private List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<MediaNode> { node };
            var chain = GetNodeChain(target, node.Children);
            if (chain.Count > 0) { chain.Insert(0, node); return chain; }
        }
        return new List<MediaNode>();
    }

    private bool IsRandomizeActive(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems); chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeCovers.HasValue)?.RandomizeCovers ?? false;
    }

    private bool IsRandomizeMusicActive(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems); chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeMusic.HasValue)?.RandomizeMusic ?? false;
    }

    private bool IsNodeInCurrentView(MediaNode modifiedNode)
    {
        if (SelectedNode == null) return false;
        if (modifiedNode == SelectedNode || modifiedNode.Id == SelectedNode.Id) return true;
        return IsChildOf(SelectedNode, modifiedNode);
    }

    private bool IsChildOf(MediaNode parent, MediaNode potentialChild)
    {
        foreach (var child in parent.Children)
        {
            if (child == potentialChild || child.Id == potentialChild.Id) return true;
            if (IsChildOf(child, potentialChild)) return true;
        }
        return false;
    }
}