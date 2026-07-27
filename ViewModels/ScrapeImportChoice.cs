using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Models;

namespace Retromind.ViewModels;

public enum ScrapeMetadataField
{
    Description,
    ReleaseDate,
    Rating,
    Developer,
    Genre,
    Platform,
    Publisher,
    Series,
    ReleaseType,
    SortTitle,
    PlayMode,
    MaxPlayers,
    Source,
    CustomField
}

public partial class ScrapeMetadataChoice : ObservableObject
{
    public ScrapeMetadataChoice(
        ScrapeMetadataField field,
        string label,
        string currentValue,
        string incomingValue,
        bool isSelected,
        string? customFieldKey = null)
    {
        Field = field;
        Label = label;
        CurrentValue = currentValue;
        IncomingValue = incomingValue;
        _isSelected = isSelected;
        CustomFieldKey = customFieldKey;
    }

    public ScrapeMetadataField Field { get; }
    public string Label { get; }
    public string CurrentValue { get; }
    public string IncomingValue { get; }
    public string? CustomFieldKey { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class ScrapeArtworkChoice : ObservableObject
{
    public ScrapeArtworkChoice(
        AssetType type,
        string label,
        string url,
        bool hasExistingArtwork,
        string statusText,
        bool isSelected)
    {
        Type = type;
        Label = label;
        Url = url;
        HasExistingArtwork = hasExistingArtwork;
        StatusText = statusText;
        _isSelected = isSelected;
    }

    public AssetType Type { get; }
    public string Label { get; }
    public string Url { get; }
    public bool HasExistingArtwork { get; }
    public string StatusText { get; }

    [ObservableProperty]
    private bool _isSelected;
}
