using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Resources; // Assuming Strings resource exists

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for configuring a MediaNode (Folder/Group).
/// Allows changing the name, default emulator, and display options.
/// </summary>
public partial class NodeSettingsViewModel : ViewModelBase
{
    private readonly MediaNode _node;
    private readonly AppSettings _settings;

    [ObservableProperty] 
    private string _name = string.Empty;

    [ObservableProperty] 
    private bool? _randomizeCovers;
    
    [ObservableProperty] 
    private bool? _randomizeMusic;

    [ObservableProperty] 
    private EmulatorConfig? _selectedEmulator;

    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    
    // Event to signal the view to close (true = saved, false = cancelled)
    public event Action<bool>? RequestClose;

    public NodeSettingsViewModel(MediaNode node, AppSettings settings)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeData();
        InitializeEmulators();

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private void InitializeData()
    {
        Name = _node.Name;
        RandomizeCovers = _node.RandomizeCovers;
        RandomizeMusic = _node.RandomizeMusic;
    }

    private void InitializeEmulators()
    {
        // Add a "None / Inherited" option
        // Ideally, move "No Default" string to resources
        AvailableEmulators.Add(new EmulatorConfig { Name = "No Default (Inherit)", Id = null! });
        
        foreach (var emu in _settings.Emulators) 
        {
            AvailableEmulators.Add(emu);
        }

        // Restore selection
        if (!string.IsNullOrEmpty(_node.DefaultEmulatorId))
        {
            SelectedEmulator = AvailableEmulators.FirstOrDefault(e => e.Id == _node.DefaultEmulatorId);
        }
        
        // Fallback to "None" if nothing selected or ID not found
        if (SelectedEmulator == null && AvailableEmulators.Count > 0) 
        {
            SelectedEmulator = AvailableEmulators[0];
        }
    }

    private void Save()
    {
        // Validation could go here (e.g. check if Name is empty)
        if (string.IsNullOrWhiteSpace(Name)) return;

        // Apply changes to the node
        _node.Name = Name;
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;
        
        // Emulator logic: If ID is null (our dummy item), set node property to null
        if (SelectedEmulator != null && SelectedEmulator.Id != null)
        {
            _node.DefaultEmulatorId = SelectedEmulator.Id;
        }
        else
        {
            _node.DefaultEmulatorId = null;
        }

        RequestClose?.Invoke(true);
    }
}