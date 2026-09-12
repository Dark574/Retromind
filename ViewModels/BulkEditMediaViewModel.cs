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

namespace Retromind.ViewModels;

public sealed record BulkEditStatusOption(PlayStatus Value, string Label);

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
    private bool _changeStatus;

    [ObservableProperty]
    private BulkEditStatusOption _selectedStatus;

    public int ItemCount { get; }
    public ObservableCollection<BulkCustomFieldPatchRow> CustomFieldPatches { get; } = new();
    public IReadOnlyList<BulkEditStatusOption> StatusOptions { get; }

    public string DialogTitle => T("BulkEdit.DialogTitle", "Edit selected items");
    public string ItemCountText => string.Format(
        T("BulkEdit.ItemCountFormat", "Changes will be applied to {0:N0} selected items."),
        ItemCount);
    public string HintText => T(
        "BulkEdit.Hint",
        "Only enabled changes are applied. All other values remain unchanged.");
    public string ChangeStatusText => T("BulkEdit.ChangeStatus", "Change status");
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

    public BulkEditMediaViewModel(int itemCount)
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

    partial void OnChangeStatusChanged(bool value) => NotifyPatchStateChanged();

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
        => (ChangeStatus || CustomFieldPatches.Any(row => !IsUnusedRow(row))) &&
           string.IsNullOrEmpty(GetValidationMessage());

    private string GetValidationMessage()
    {
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
            ChangeStatus = ChangeStatus,
            Status = SelectedStatus.Value,
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
