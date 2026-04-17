using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;

namespace Retromind.ViewModels;

public sealed record SearchQueryBuilderResult(bool WasApplied, bool ReplaceSearch, string Token);

public partial class SearchQueryBuilderViewModel : ViewModelBase
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _suggestionsByField;
    private const double BaseFieldComboMinWidth = 280;
    private const double MaxFieldComboMinWidth = 420;

    public ObservableCollection<SearchQueryFieldOption> FieldOptions { get; } = new();
    public ObservableCollection<string> ValueSuggestions { get; } = new();
    public double FieldComboMinWidth { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSearchCommand))]
    private SearchQueryFieldOption? _selectedField;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSearchCommand))]
    private string _valueText = string.Empty;

    public IRelayCommand AddTokenCommand { get; }
    public IRelayCommand ReplaceSearchCommand { get; }

    public event Action<SearchQueryBuilderResult>? RequestClose;

    public SearchQueryBuilderViewModel(
        IEnumerable<SearchQueryFieldOption> fields,
        IReadOnlyDictionary<string, IReadOnlyList<string>> suggestionsByField)
    {
        _suggestionsByField = suggestionsByField
                              ?? throw new ArgumentNullException(nameof(suggestionsByField));

        foreach (var field in fields ?? throw new ArgumentNullException(nameof(fields)))
            FieldOptions.Add(field);

        FieldComboMinWidth = CalculateFieldComboMinWidth(FieldOptions);

        AddTokenCommand = new RelayCommand(() => Submit(replaceSearch: false), CanSubmit);
        ReplaceSearchCommand = new RelayCommand(() => Submit(replaceSearch: true), CanSubmit);

        SelectedField = FieldOptions.FirstOrDefault();
    }

    private static double CalculateFieldComboMinWidth(IEnumerable<SearchQueryFieldOption> fields)
    {
        var widest = fields
            .Select(f => f?.Label?.Trim() ?? string.Empty)
            .Select(EstimateTextWidth)
            .DefaultIfEmpty(0d)
            .Max();

        var withPadding = widest + 72; // dropdown glyph + left/right padding
        return Math.Clamp(withPadding, BaseFieldComboMinWidth, MaxFieldComboMinWidth);
    }

    private static double EstimateTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0d;
        foreach (var ch in text)
        {
            width += ch switch
            {
                'i' or 'l' or 'I' or '.' or ',' or '!' or '\'' => 3.5,
                'm' or 'w' or 'M' or 'W' => 10.5,
                ' ' => 4,
                _ => 7
            };
        }

        return width;
    }

    partial void OnSelectedFieldChanged(SearchQueryFieldOption? value)
    {
        RefreshValueSuggestions();
    }

    private bool CanSubmit()
    {
        return SelectedField != null && !string.IsNullOrWhiteSpace(ValueText);
    }

    private void Submit(bool replaceSearch)
    {
        if (!CanSubmit() || SelectedField == null)
            return;

        var token = SearchQueryBuilderHelper.BuildToken(SelectedField.Key, ValueText);
        RequestClose?.Invoke(new SearchQueryBuilderResult(true, replaceSearch, token));
    }

    private void RefreshValueSuggestions()
    {
        ValueSuggestions.Clear();
        if (SelectedField == null)
            return;

        if (!_suggestionsByField.TryGetValue(SelectedField.Key, out var values))
            return;

        foreach (var value in values)
            ValueSuggestions.Add(value);
    }
}
