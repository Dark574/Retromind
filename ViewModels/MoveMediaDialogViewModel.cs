using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public sealed partial class MoveMediaDialogViewModel : ViewModelBase
{
    public ObservableCollection<MoveMediaNodeOption> RootNodes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private MoveMediaNodeOption? _selectedOption;

    public string DialogTitle => R("MoveMedia.DialogTitle", "Move item");
    public string InstructionText => R("MoveMedia.Instruction", "Select the target category:");
    public string CancelText => R("Button.Cancel", "Cancel");
    public string ApplyText => R("MoveMedia.Apply", "Move");

    public string ValidationMessage => SelectedOption == null
        ? R("MoveMedia.SelectTarget", "Please select a target category.")
        : GetValidationMessage(SelectedOption.Assessment.Status);

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public MoveMediaDialogViewModel(
        IEnumerable<MediaNode> rootNodes,
        MediaItem item,
        MediaNode sourceNode)
    {
        foreach (var rootNode in rootNodes)
        {
            var option = BuildOption(rootNode, item, sourceNode, isRoot: true);
            if (option != null)
                RootNodes.Add(option);
        }

        ApplyCommand = new RelayCommand(
            () => RequestClose?.Invoke(true),
            () => SelectedOption?.Assessment.IsAllowed == true);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public MediaNode? GetSelectedTarget()
        => SelectedOption?.Assessment.IsAllowed == true ? SelectedOption.Node : null;

    private static MoveMediaNodeOption? BuildOption(
        MediaNode node,
        MediaItem item,
        MediaNode sourceNode,
        bool isRoot)
    {
        if (!node.IsVisibleInTree)
            return null;

        var option = new MoveMediaNodeOption(
            node,
            MediaItemMovePolicy.Assess(item, sourceNode, node));

        var containsSource = ReferenceEquals(node, sourceNode) || node.Id == sourceNode.Id;
        foreach (var child in node.Children)
        {
            var childOption = BuildOption(child, item, sourceNode, isRoot: false);
            if (childOption == null)
                continue;

            option.Children.Add(childOption);
            containsSource |= childOption.ContainsSource;
        }

        option.ContainsSource = containsSource;
        option.IsExpanded = isRoot || node.IsExpanded || containsSource;
        return option;
    }

    private static string GetValidationMessage(MediaItemMoveTargetStatus status)
    {
        return status switch
        {
            MediaItemMoveTargetStatus.Allowed => string.Empty,
            MediaItemMoveTargetStatus.CurrentNode =>
                R("MoveMedia.CurrentCategory", "The item is already in this category."),
            MediaItemMoveTargetStatus.StoreProviderMismatch =>
                R("MoveMedia.StoreProviderMismatch", "This synchronized store category only accepts matching store items."),
            MediaItemMoveTargetStatus.MissingStoreIdentity =>
                R("MoveMedia.MissingStoreIdentity", "The item does not contain the complete identity required by this store category."),
            MediaItemMoveTargetStatus.DuplicateStoreItem =>
                R("MoveMedia.DuplicateStoreItem", "This store item already exists in the selected category."),
            _ => R("MoveMedia.InvalidTarget", "The selected category cannot be used as a target.")
        };
    }

    private static string R(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;
}

public sealed partial class MoveMediaNodeOption : ObservableObject
{
    public MediaNode Node { get; }
    internal MediaItemMoveTargetAssessment Assessment { get; }
    public ObservableCollection<MoveMediaNodeOption> Children { get; } = new();
    public string DisplayName => Node.Name;

    [ObservableProperty]
    private bool _isExpanded;

    internal bool ContainsSource { get; set; }

    internal MoveMediaNodeOption(MediaNode node, MediaItemMoveTargetAssessment assessment)
    {
        Node = node;
        Assessment = assessment;
    }
}
