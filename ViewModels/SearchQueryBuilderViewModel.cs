using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Resources;

namespace Retromind.ViewModels;

public sealed record SearchQueryBuilderResult(
    bool WasApplied,
    bool ReplaceSearch,
    string Token,
    string? JoinOperator = null);

public sealed record SearchJoinOperatorOption(string Key, string Label);
public sealed record SearchMatchModeOption(string Key, string Label);

public partial class SearchQueryBuilderViewModel : ViewModelBase
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _suggestionsByField;
    private const double BaseFieldComboMinWidth = 280;
    private const double MaxFieldComboMinWidth = 420;

    public ObservableCollection<SearchQueryFieldOption> FieldOptions { get; } = new();
    public ObservableCollection<SearchJoinOperatorOption> JoinOperatorOptions { get; } = new();
    public ObservableCollection<SearchMatchModeOption> MatchModeOptions { get; } = new();
    public ObservableCollection<string> ValueSuggestions { get; } = new();
    public double FieldComboMinWidth { get; }

    public string JoinLabel { get; }
    public string MatchModeLabel { get; }
    public string NegateLabel { get; }
    public string GroupLabel { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSearchCommand))]
    private SearchQueryFieldOption? _selectedField;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSearchCommand))]
    private string _valueText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSearchCommand))]
    private SearchMatchModeOption? _selectedMatchMode;

    [ObservableProperty]
    private SearchJoinOperatorOption? _selectedJoinOperator;

    [ObservableProperty]
    private bool _negateToken;

    [ObservableProperty]
    private bool _wrapTokenInParentheses;

    public bool IsValueInputEnabled =>
        string.Equals(SelectedMatchMode?.Key, "contains", StringComparison.OrdinalIgnoreCase);

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

        JoinLabel = T("Search.FilterBuilderJoin", "Join");
        MatchModeLabel = T("Search.FilterBuilderMode", "Match");
        NegateLabel = T("Search.FilterBuilderNot", "Negate (NOT)");
        GroupLabel = T("Search.FilterBuilderGroup", "Wrap in parentheses");

        MatchModeOptions.Add(new SearchMatchModeOption("contains", T("Search.FilterBuilderModeContains", "Contains")));
        MatchModeOptions.Add(new SearchMatchModeOption("has", T("Search.FilterBuilderModeHas", "Has value")));
        MatchModeOptions.Add(new SearchMatchModeOption("missing", T("Search.FilterBuilderModeMissing", "Is missing")));

        JoinOperatorOptions.Add(new SearchJoinOperatorOption("AND", T("Search.FilterBuilderJoinAnd", "AND")));
        JoinOperatorOptions.Add(new SearchJoinOperatorOption("OR", T("Search.FilterBuilderJoinOr", "OR")));

        FieldComboMinWidth = CalculateFieldComboMinWidth(FieldOptions);

        AddTokenCommand = new RelayCommand(() => Submit(replaceSearch: false), CanSubmit);
        ReplaceSearchCommand = new RelayCommand(() => Submit(replaceSearch: true), CanSubmit);

        SelectedField = FieldOptions.FirstOrDefault();
        SelectedMatchMode = MatchModeOptions.FirstOrDefault();
        SelectedJoinOperator = JoinOperatorOptions.FirstOrDefault();
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

    partial void OnSelectedMatchModeChanged(SearchMatchModeOption? value)
    {
        OnPropertyChanged(nameof(IsValueInputEnabled));
        AddTokenCommand.NotifyCanExecuteChanged();
        ReplaceSearchCommand.NotifyCanExecuteChanged();
    }

    private bool CanSubmit()
    {
        if (SelectedField == null || SelectedMatchMode == null)
            return false;

        if (!IsValueInputEnabled)
            return true;

        return !string.IsNullOrWhiteSpace(ValueText);
    }

    private void Submit(bool replaceSearch)
    {
        if (!CanSubmit() || SelectedField == null)
            return;

        var token = BuildToken();
        if (NegateToken)
            token = $"NOT {token}";
        if (WrapTokenInParentheses)
            token = $"({token})";

        RequestClose?.Invoke(new SearchQueryBuilderResult(
            true,
            replaceSearch,
            token,
            SelectedJoinOperator?.Key));
    }

    private string BuildToken()
    {
        var fieldKey = SelectedField?.Key ?? string.Empty;
        var mode = SelectedMatchMode?.Key ?? "contains";
        if (string.Equals(mode, "has", StringComparison.OrdinalIgnoreCase))
            return $"has:{fieldKey}";
        if (string.Equals(mode, "missing", StringComparison.OrdinalIgnoreCase))
            return $"missing:{fieldKey}";

        return SearchQueryBuilderHelper.BuildToken(fieldKey, ValueText);
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

    private static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
