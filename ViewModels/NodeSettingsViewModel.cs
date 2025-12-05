using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class NodeSettingsViewModel : ViewModelBase
{
    private readonly MediaNode _node;
    private readonly AppSettings _settings;

    // Durch [ObservableProperty] werden automatisch die öffentlichen Properties generiert:
    // _name -> Name
    // _randomizeCovers -> RandomizeCovers
    // _selectedEmulator -> SelectedEmulator
    
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool? _randomizeCovers;
    [ObservableProperty] private bool? _randomizeMusic;
    [ObservableProperty] private EmulatorConfig? _selectedEmulator;

    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public event Action<bool>? RequestClose;

    public NodeSettingsViewModel(MediaNode node, AppSettings settings)
    {
        _node = node;
        _settings = settings;

        // Initialwerte laden
        _name = node.Name;
        _randomizeCovers = node.RandomizeCovers;
        _randomizeMusic = node.RandomizeMusic;

        // Emulatoren laden
        AvailableEmulators.Add(new EmulatorConfig { Name = "Kein Standard (Vererbt)", Id = null! });
        foreach (var emu in _settings.Emulators) AvailableEmulators.Add(emu);

        // Selektierten Emulator wiederherstellen
        if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
            _selectedEmulator = _settings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
        
        if (_selectedEmulator == null) _selectedEmulator = AvailableEmulators[0];

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private void Save()
    {
        // Werte zurück in den Node schreiben
        _node.Name = Name;
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;
        
        if (SelectedEmulator != null && SelectedEmulator.Id != null)
            _node.DefaultEmulatorId = SelectedEmulator.Id;
        else
            _node.DefaultEmulatorId = null;

        RequestClose?.Invoke(true);
    }
}