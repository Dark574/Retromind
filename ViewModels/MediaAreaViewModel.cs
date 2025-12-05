using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

// Erbt jetzt von ViewModelBase (oder ObservableObject), damit Bindings funktionieren
public partial class MediaAreaViewModel : ViewModelBase
{
    // Slider-Wert für die Kachelgröße (Standard 150)
    [ObservableProperty] private double _itemWidth = 150;

    [ObservableProperty] private MediaNode _node;

    // Das aktuell ausgewählte Spiel/Medium in der Liste
    [ObservableProperty] private MediaItem? _selectedMediaItem;

    // Dieser Konstruktor wird aufgerufen!
    public MediaAreaViewModel(MediaNode node, double initialItemWidth)
    {
        Node = node;
        ItemWidth = initialItemWidth;

        // Command initialisieren
        DoubleClickCommand = new RelayCommand(OnDoubleClick);
    }

    // Command, das von der View (Doppelklick) aufgerufen wird
    public ICommand DoubleClickCommand { get; }

    // Event, um dem Parent (MainWindowViewModel) zu sagen: "Start das Ding!"
    public event Action<MediaItem>? RequestPlay;

    private void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
            // Feuere das Event -> MainWindowViewModel fängt das und startet den Launcher
            RequestPlay?.Invoke(SelectedMediaItem);
    }
}