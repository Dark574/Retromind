using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public sealed record BulkEditStatusOption(PlayStatus Value, string Label);

public enum BulkMetadataEditOperation
{
    NoChange,
    Set,
    Clear
}

public sealed record BulkMetadataOperationOption(
    BulkMetadataEditOperation Value,
    string Label);

public sealed partial class BulkTextMetadataPatchRow : ObservableObject
{
    private readonly MetadataSuggestionService _suggestionService;
    private readonly string? _suggestionFieldKey;

    public TextMetadataField Field { get; }
    public string Label { get; }
    public IReadOnlyList<BulkMetadataOperationOption> OperationOptions { get; }

    [ObservableProperty]
    private BulkMetadataOperationOption _selectedOperation;

    [ObservableProperty]
    private string _value = string.Empty;

    public string SuggestionSuffix => GetSuggestionSuffix(
        Value,
        _suggestionFieldKey == null
            ? null
            : _suggestionService.GetBestMatch(_suggestionFieldKey, Value));
    public bool IsSetOperation => SelectedOperation.Value == BulkMetadataEditOperation.Set;
    public bool HasChange => SelectedOperation.Value != BulkMetadataEditOperation.NoChange;
    public IRelayCommand AcceptSuggestionCommand { get; }

    public BulkTextMetadataPatchRow(
        TextMetadataField field,
        string label,
        IReadOnlyList<BulkMetadataOperationOption> operationOptions,
        MetadataSuggestionService suggestionService,
        string? suggestionFieldKey)
    {
        Field = field;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        OperationOptions = operationOptions ?? throw new ArgumentNullException(nameof(operationOptions));
        _suggestionService = suggestionService ?? throw new ArgumentNullException(nameof(suggestionService));
        _suggestionFieldKey = suggestionFieldKey;
        _selectedOperation = OperationOptions.First(option =>
            option.Value == BulkMetadataEditOperation.NoChange);
        AcceptSuggestionCommand = new RelayCommand(
            AcceptSuggestion,
            () => !string.IsNullOrEmpty(SuggestionSuffix));
    }

    partial void OnSelectedOperationChanged(BulkMetadataOperationOption value)
    {
        OnPropertyChanged(nameof(IsSetOperation));
        OnPropertyChanged(nameof(HasChange));
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SuggestionSuffix));
        AcceptSuggestionCommand.NotifyCanExecuteChanged();
    }

    private void AcceptSuggestion()
    {
        if (_suggestionFieldKey == null)
            return;

        var suggestion = _suggestionService.GetBestMatch(_suggestionFieldKey, Value);
        if (!string.IsNullOrEmpty(GetSuggestionSuffix(Value, suggestion)))
            Value = suggestion!;
    }

    private static string GetSuggestionSuffix(string? input, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(suggestion))
            return string.Empty;

        var trimmedInput = input.Trim();
        if (!suggestion.StartsWith(trimmedInput, StringComparison.OrdinalIgnoreCase) ||
            trimmedInput.Length >= suggestion.Length)
        {
            return string.Empty;
        }

        return suggestion[trimmedInput.Length..];
    }
}

public sealed record CustomFieldPatchOperationOption(
    CustomFieldPatchOperation Value,
    string Label);

public sealed partial class BulkCustomFieldPatchRow : ObservableObject
{
    public IReadOnlyList<CustomFieldPatchOperationOption> OperationOptions { get; }
    public string DiscardChangeText { get; }

    [ObservableProperty]
    private CustomFieldPatchOperationOption _selectedOperation;

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    public bool IsSetOperation => SelectedOperation.Value == CustomFieldPatchOperation.Set;

    public BulkCustomFieldPatchRow(
        IReadOnlyList<CustomFieldPatchOperationOption> operationOptions,
        string discardChangeText)
    {
        OperationOptions = operationOptions ?? throw new ArgumentNullException(nameof(operationOptions));
        DiscardChangeText = discardChangeText ?? throw new ArgumentNullException(nameof(discardChangeText));
        _selectedOperation = OperationOptions.First(option =>
            option.Value == CustomFieldPatchOperation.Set);
    }

    partial void OnSelectedOperationChanged(CustomFieldPatchOperationOption value)
        => OnPropertyChanged(nameof(IsSetOperation));
}

public sealed partial class BulkEditMediaViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CustomFieldPatchOperationOption> _operationOptions;

    [ObservableProperty]
    private BulkEditStatusOption _selectedStatus;

    [ObservableProperty]
    private BulkMetadataOperationOption _selectedStatusOperation = null!;

    public int ItemCount { get; }
    public IReadOnlyList<BulkTextMetadataPatchRow> TextMetadataFields { get; }
    public ObservableCollection<BulkCustomFieldPatchRow> CustomFieldPatches { get; } = new();
    public IReadOnlyList<BulkEditStatusOption> StatusOptions { get; }
    public IReadOnlyList<BulkMetadataOperationOption> MetadataOperationOptions { get; }
    public IReadOnlyList<BulkMetadataOperationOption> StatusOperationOptions { get; }

    public bool IsStatusSetOperation =>
        SelectedStatusOperation.Value == BulkMetadataEditOperation.Set;
    public bool HasStatusChange =>
        SelectedStatusOperation.Value != BulkMetadataEditOperation.NoChange;

    public string DialogTitle => T("BulkEdit.DialogTitle", "Edit selected items");
    public string ItemCountText => string.Format(
        T("BulkEdit.ItemCountFormat", "Changes will be applied to {0:N0} selected items."),
        ItemCount);
    public string HintText => T(
        "BulkEdit.Hint",
        "Only enabled changes are applied. All other values remain unchanged.");
    public string MetadataTabTitle => T("BulkEdit.Metadata.TabTitle", "Metadata");
    public string MetadataHint => T(
        "BulkEdit.Metadata.Hint",
        "Choose an action only for fields you want to change.");
    public string StatusText => T("Common.Status", "Status");
    public string CustomFieldsTitle => T("Common.CustomFields", "Custom fields");
    public string CustomFieldsHint => T(
        "BulkEdit.CustomFields.Hint",
        "Add a row to set or remove the same field on all selected items.");
    public string AddCustomFieldText => T("BulkEdit.CustomFields.Add", "Add field change");
    public string KeyPlaceholderText => T("Common.Key", "Key");
    public string ValuePlaceholderText => T("Common.Value", "Value");
    public string DiscardCustomFieldPatchText => T(
        "BulkEdit.CustomFields.DiscardChange",
        "Discard this field change");
    public string CancelText => T("Button.Cancel", "Cancel");
    public string ApplyText => T("BulkEdit.Apply", "Apply changes");

    public string ValidationMessage => GetValidationMessage();
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public MediaBulkEditPatch? ResultPatch { get; private set; }

    public IRelayCommand AddCustomFieldPatchCommand { get; }
    public IRelayCommand<BulkCustomFieldPatchRow?> RemoveCustomFieldPatchCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public BulkEditMediaViewModel(int itemCount, IEnumerable<MediaNode>? rootNodes = null)
    {
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));

        ItemCount = itemCount;
        StatusOptions =
        [
            new(PlayStatus.Incomplete, T("BulkEdit.Status.Incomplete", "Incomplete")),
            new(PlayStatus.Completed, T("BulkEdit.Status.Completed", "Completed")),
            new(PlayStatus.Abandoned, T("BulkEdit.Status.Abandoned", "Abandoned"))
        ];
        _selectedStatus = StatusOptions[0];

        MetadataOperationOptions =
        [
            new(BulkMetadataEditOperation.NoChange, T("BulkEdit.Metadata.NoChange", "Do not change")),
            new(BulkMetadataEditOperation.Set, T("BulkEdit.Metadata.Set", "Set")),
            new(BulkMetadataEditOperation.Clear, T("BulkEdit.Metadata.Clear", "Clear"))
        ];
        StatusOperationOptions = MetadataOperationOptions
            .Where(option => option.Value != BulkMetadataEditOperation.Clear)
            .ToList();
        _selectedStatusOperation = StatusOperationOptions[0];

        var suggestionService = new MetadataSuggestionService(rootNodes);

        TextMetadataFields =
        [
            CreateMetadataRow(TextMetadataField.Developer, "Common.Developer", "Developer", suggestionService, MetadataSuggestionService.DeveloperField),
            CreateMetadataRow(TextMetadataField.Publisher, "Common.Publisher", "Publisher", suggestionService, MetadataSuggestionService.PublisherField),
            CreateMetadataRow(TextMetadataField.Platform, "Common.Platform", "Platform", suggestionService, MetadataSuggestionService.PlatformField),
            CreateMetadataRow(TextMetadataField.Source, "Common.Source", "Source", suggestionService, null),
            CreateMetadataRow(TextMetadataField.Genre, "Common.Genre", "Genre", suggestionService, MetadataSuggestionService.GenreField),
            CreateMetadataRow(TextMetadataField.Series, "Common.Series", "Series", suggestionService, MetadataSuggestionService.SeriesField),
            CreateMetadataRow(TextMetadataField.ReleaseType, "Common.ReleaseType", "Release type", suggestionService, MetadataSuggestionService.ReleaseTypeField),
            CreateMetadataRow(TextMetadataField.PlayMode, "Common.PlayMode", "Play mode", suggestionService, MetadataSuggestionService.PlayModeField),
            CreateMetadataRow(TextMetadataField.MaxPlayers, "Common.MaxPlayers", "Max players", suggestionService, null)
        ];
        foreach (var row in TextMetadataFields)
            row.PropertyChanged += OnMetadataFieldChanged;

        _operationOptions =
        [
            new(CustomFieldPatchOperation.Set, T("BulkEdit.CustomFields.Set", "Set")),
            new(CustomFieldPatchOperation.Remove, T("BulkEdit.CustomFields.Remove", "Remove"))
        ];

        AddCustomFieldPatchCommand = new RelayCommand(AddCustomFieldPatch);
        RemoveCustomFieldPatchCommand = new RelayCommand<BulkCustomFieldPatchRow?>(RemoveCustomFieldPatch);
        ApplyCommand = new RelayCommand(Apply, CanApply);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        CustomFieldPatches.CollectionChanged += OnCustomFieldPatchesChanged;
    }

    partial void OnSelectedStatusOperationChanged(BulkMetadataOperationOption value)
    {
        OnPropertyChanged(nameof(IsStatusSetOperation));
        OnPropertyChanged(nameof(HasStatusChange));
        NotifyPatchStateChanged();
    }

    private BulkTextMetadataPatchRow CreateMetadataRow(
        TextMetadataField field,
        string resourceKey,
        string fallback,
        MetadataSuggestionService suggestionService,
        string? suggestionFieldKey)
        => new(
            field,
            T(resourceKey, fallback),
            MetadataOperationOptions,
            suggestionService,
            suggestionFieldKey);

    private void OnMetadataFieldChanged(object? sender, PropertyChangedEventArgs e)
        => NotifyPatchStateChanged();

    private void AddCustomFieldPatch()
        => CustomFieldPatches.Add(new BulkCustomFieldPatchRow(
            _operationOptions,
            DiscardCustomFieldPatchText));

    private void RemoveCustomFieldPatch(BulkCustomFieldPatchRow? row)
    {
        if (row != null)
            CustomFieldPatches.Remove(row);
    }

    private void OnCustomFieldPatchesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var row in e.OldItems.OfType<BulkCustomFieldPatchRow>())
                row.PropertyChanged -= OnCustomFieldPatchChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var row in e.NewItems.OfType<BulkCustomFieldPatchRow>())
                row.PropertyChanged += OnCustomFieldPatchChanged;
        }

        NotifyPatchStateChanged();
    }

    private void OnCustomFieldPatchChanged(object? sender, PropertyChangedEventArgs e)
        => NotifyPatchStateChanged();

    private void NotifyPatchStateChanged()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationMessage));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply()
        => (HasStatusChange ||
            TextMetadataFields.Any(row => row.HasChange) ||
            CustomFieldPatches.Any(row => !IsUnusedRow(row))) &&
           string.IsNullOrEmpty(GetValidationMessage());

    private string GetValidationMessage()
    {
        var incompleteMetadataField = TextMetadataFields.FirstOrDefault(row =>
            row.IsSetOperation && string.IsNullOrWhiteSpace(row.Value));
        if (incompleteMetadataField != null)
        {
            return string.Format(
                T("BulkEdit.Validation.MetadataValueRequired", "Enter a value for {0} or choose Clear."),
                incompleteMetadataField.Label);
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CustomFieldPatches)
        {
            if (IsUnusedRow(row))
                continue;

            var key = row.Key.Trim();
            if (string.IsNullOrWhiteSpace(key))
                return T("BulkEdit.Validation.KeyRequired", "Enter a key for every field change.");

            if (CustomFieldKeyHelper.IsInternal(key))
            {
                return T(
                    "BulkEdit.Validation.InternalKey",
                    "Internal Store.* fields cannot be changed here.");
            }

            if (!seenKeys.Add(key))
            {
                return T(
                    "BulkEdit.Validation.DuplicateKey",
                    "Each custom field may only occur once.");
            }

            if (row.IsSetOperation && string.IsNullOrWhiteSpace(row.Value))
            {
                return T(
                    "BulkEdit.Validation.ValueRequired",
                    "Enter a value or select Remove.");
            }
        }

        return string.Empty;
    }

    private static bool IsUnusedRow(BulkCustomFieldPatchRow row)
        => row.IsSetOperation &&
           string.IsNullOrWhiteSpace(row.Key) &&
           string.IsNullOrWhiteSpace(row.Value);

    private void Apply()
    {
        if (!CanApply())
            return;

        ResultPatch = new MediaBulkEditPatch
        {
            ChangeStatus = HasStatusChange,
            Status = SelectedStatus.Value,
            TextMetadata = TextMetadataFields
                .Where(row => row.HasChange)
                .Select(row => new TextMetadataPatch(
                    row.Field,
                    row.SelectedOperation.Value == BulkMetadataEditOperation.Clear
                        ? MetadataPatchOperation.Clear
                        : MetadataPatchOperation.Set,
                    row.IsSetOperation ? row.Value.Trim() : null))
                .ToList(),
            CustomFields = CustomFieldPatches
                .Where(row => !IsUnusedRow(row))
                .Select(row => new CustomFieldPatch(
                    row.SelectedOperation.Value,
                    row.Key.Trim(),
                    row.IsSetOperation ? row.Value.Trim() : null))
                .ToList()
        };

        RequestClose?.Invoke(true);
    }
}
