using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

// Erben von ObservableObject für Property-Change-Notifications
public partial class MediaNode : ObservableObject
{
    [ObservableProperty] private bool _isExpanded; // Speichert den Klapp-Zustand

    // ObservableProperty generiert automatisch Name, IsExpanded etc.
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private NodeType _type;

    // Parameterloser Konstruktor für JSON Deserializer
    public MediaNode()
    {
    }

    public MediaNode(string name, NodeType type)
    {
        Name = name;
        Type = type;
        IsExpanded = true; // Neue Gruppen standardmäßig aufklappen
    }

    // Eindeutige ID für Wiederherstellung
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // null = Erben, true = An, false = Aus
    [ObservableProperty] private bool? _randomizeCovers;
    [ObservableProperty] private bool? _randomizeMusic;
    
    // ID des Emulators, der für diesen Ordner gilt (optional)
    public string? DefaultEmulatorId { get; set; }

    // Initialisierung der Liste, damit sie nie null ist
    public ObservableCollection<MediaNode> Children { get; set; } = new();

    // Die Medien in diesem Ordner
    public ObservableCollection<MediaItem> Items { get; set; } = new();
}

public enum NodeType
{
    Area,
    Group
}