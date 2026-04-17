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

    public ObservableCollection<SearchQueryFieldOption> FieldOptions { get; } = new();
    public ObservableCollection<string> ValueSuggestions { get; } = new();

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

        AddTokenCommand = new RelayCommand(() => Submit(replaceSearch: false), CanSubmit);
        ReplaceSearchCommand = new RelayCommand(() => Submit(replaceSearch: true), CanSubmit);

        SelectedField = FieldOptions.FirstOrDefault();
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
