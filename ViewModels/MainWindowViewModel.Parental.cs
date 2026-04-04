using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool _isApplyingProtectionChanges;
    private CancellationTokenSource? _parentalRefreshCts;
    private readonly TimeSpan _parentalRefreshDebounce = TimeSpan.FromMilliseconds(120);

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    public bool HasParentalPassword =>
        !string.IsNullOrWhiteSpace(_currentSettings.ParentalLockPasswordEncrypted);

    public bool IsParentalFilterActive =>
        ParentalControlHelper.IsFilterActive(_currentSettings);

    public string ParentalLockButtonText =>
        !HasParentalPassword
            ? T("Parental.Lock.Setup", "Setup Lock")
            : IsParentalFilterActive
                ? T("Parental.Lock.Unlock", "Unlock")
                : T("Parental.Lock.Lock", "Lock");

    public string ParentalLockToolTip =>
        !HasParentalPassword
            ? T("Parental.Lock.Tooltip.Setup", "Set up parental lock password")
            : IsParentalFilterActive
                ? T("Parental.Lock.Tooltip.Unlock", "Unlock protected media")
                : T("Parental.Lock.Tooltip.Lock", "Lock protected media");

    public string ParentalProtectButtonToolTip =>
        T("Parental.ProtectButton.Tooltip", "Toggle parental protection for this item");

    public bool ShowParentalProtectionButton => !IsParentalFilterActive;

    private void InitializeParentalStateAfterLoad()
    {
        if (!HasParentalPassword)
            _currentSettings.ParentalLockUnlocked = true;

        RefreshTreeVisibility();
        NotifyParentalStateChanged();
    }

    private void NotifyParentalStateChanged()
    {
        OnPropertyChanged(nameof(HasParentalPassword));
        OnPropertyChanged(nameof(IsParentalFilterActive));
        OnPropertyChanged(nameof(ParentalLockButtonText));
        OnPropertyChanged(nameof(ParentalLockToolTip));
        OnPropertyChanged(nameof(ParentalProtectButtonToolTip));
        OnPropertyChanged(nameof(ShowParentalProtectionButton));
    }

    private bool CanShowItemByParentalFilter(MediaItem item)
        => ParentalControlHelper.CanShowItem(item, IsParentalFilterActive);

    private void RefreshTreeVisibility()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(RefreshTreeVisibility, DispatcherPriority.Background);
            return;
        }

        var filterActive = IsParentalFilterActive;
        foreach (var root in RootItems)
            UpdateTreeVisibilityRecursive(root, filterActive);
    }

    private static bool UpdateTreeVisibilityRecursive(MediaNode node, bool filterActive)
    {
        var visibleChildExists = false;
        foreach (var child in node.Children)
        {
            if (UpdateTreeVisibilityRecursive(child, filterActive))
                visibleChildExists = true;
        }

        var visibleItemExists = !filterActive || node.Items.Any(item => !item.IsProtected);
        var isEmptyNode = node.Children.Count == 0 && node.Items.Count == 0;
        var visible = !filterActive || isEmptyNode || visibleItemExists || visibleChildExists;

        node.IsVisibleInTree = visible;
        return visible;
    }

    private MediaNode? FindFirstVisibleNode()
    {
        foreach (var root in RootItems)
        {
            var visible = FindFirstVisibleNodeRecursive(root);
            if (visible != null)
                return visible;
        }

        return null;
    }

    private static MediaNode? FindFirstVisibleNodeRecursive(MediaNode node)
    {
        if (node.IsVisibleInTree)
            return node;

        foreach (var child in node.Children)
        {
            var visible = FindFirstVisibleNodeRecursive(child);
            if (visible != null)
                return visible;
        }

        return null;
    }

    private void RefreshParentalFilteringState(bool refreshCurrentContent = true)
    {
        RefreshTreeVisibility();
        NotifyParentalStateChanged();

        if (SelectedNode != null && !SelectedNode.IsVisibleInTree)
        {
            var fallbackNode = FindFirstVisibleNode();
            if (fallbackNode != null)
            {
                ExpandPathToNode(RootItems, fallbackNode);
                SelectedNode = fallbackNode;
            }
            else
            {
                SelectedNode = null;
                SelectedNodeContent = null;
            }
        }
        else if (refreshCurrentContent && SelectedNodeContent is MediaAreaViewModel && SelectedNode != null)
        {
            UpdateContent();
        }

        if (SelectedNodeContent is SearchAreaViewModel searchVm)
            searchVm.SetParentalFilterActive(IsParentalFilterActive);
    }

    private void ScheduleParentalProtectionRefresh()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(ScheduleParentalProtectionRefresh, DispatcherPriority.Background);
            return;
        }

        _parentalRefreshCts?.Cancel();
        _parentalRefreshCts?.Dispose();
        _parentalRefreshCts = new CancellationTokenSource();

        var token = _parentalRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_parentalRefreshDebounce, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (_isApplyingProtectionChanges)
                        return;

                    RecalculateAllAutoProtectStates();
                    RefreshParentalFilteringState();
                }, DispatcherPriority.Background).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while protection state changes are still flowing in.
            }
        }, token);
    }

    private static bool AreAllItemsProtectedInSubtree(MediaNode node, out bool hasAnyItems)
    {
        hasAnyItems = false;
        var allProtected = true;

        foreach (var item in node.Items)
        {
            hasAnyItems = true;
            if (!item.IsProtected)
                allProtected = false;
        }

        foreach (var child in node.Children)
        {
            var childAllProtected = AreAllItemsProtectedInSubtree(child, out var childHasItems);
            if (!childHasItems)
                continue;

            hasAnyItems = true;
            if (!childAllProtected)
                allProtected = false;
        }

        return allProtected;
    }

    private bool IsNodeEffectivelyProtected(MediaNode node)
    {
        var allProtected = AreAllItemsProtectedInSubtree(node, out var hasAnyItems);
        return hasAnyItems ? allProtected : node.AutoProtectNewChildren;
    }

    private void RecalculateAllAutoProtectStates()
    {
        foreach (var root in RootItems)
            RecalculateAutoProtectStateRecursive(root, out _);
    }

    private static bool RecalculateAutoProtectStateRecursive(MediaNode node, out bool hasAnyItems)
    {
        hasAnyItems = false;
        var allProtected = true;

        foreach (var item in node.Items)
        {
            hasAnyItems = true;
            if (!item.IsProtected)
                allProtected = false;
        }

        foreach (var child in node.Children)
        {
            var childAllProtected = RecalculateAutoProtectStateRecursive(child, out var childHasItems);
            if (!childHasItems)
                continue;

            hasAnyItems = true;
            if (!childAllProtected)
                allProtected = false;
        }

        if (hasAnyItems)
            node.AutoProtectNewChildren = allProtected;

        return allProtected;
    }

    private bool IsAutoProtectActiveForNode(MediaNode node)
    {
        var chain = GetNodeChain(node, RootItems);
        chain.Reverse();
        return chain.FirstOrDefault(n => n.AutoProtectNewChildren)?.AutoProtectNewChildren ?? false;
    }

    private void ApplyEffectiveParentalProtection(MediaNode targetNode, IEnumerable<MediaItem> items)
    {
        if (targetNode == null || items == null)
            return;

        if (!IsAutoProtectActiveForNode(targetNode))
            return;

        foreach (var item in items)
            item.IsProtected = true;
    }

    private void ApplyNodeProtectionRecursive(MediaNode node, bool isProtected)
    {
        node.AutoProtectNewChildren = isProtected;

        foreach (var item in node.Items)
            item.IsProtected = isProtected;

        foreach (var child in node.Children)
            ApplyNodeProtectionRecursive(child, isProtected);
    }

    private void RefreshAncestorAutoProtectStates(MediaNode? node)
    {
        if (node == null)
            return;

        var chain = GetNodeChain(node, RootItems);
        if (chain.Count == 0)
            return;

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var current = chain[i];
            var allProtected = AreAllItemsProtectedInSubtree(current, out var hasAnyItems);
            if (hasAnyItems)
                current.AutoProtectNewChildren = allProtected;
        }
    }

    private void FinalizeProtectionMutation()
    {
        MarkLibraryDirtyAndSaveSoon();
        RefreshParentalFilteringState();
    }

    private async Task<bool> EnsureParentalPasswordConfiguredAsync(Window owner)
    {
        if (HasParentalPassword)
            return true;

        var password = await PromptForPasswordAsync(
            owner,
            T("Parental.Dialog.Title", "Parental Lock"),
            T("Parental.Dialog.SetupMessage", "Set a password for parental controls."),
            requireConfirmation: true);

        if (string.IsNullOrWhiteSpace(password))
            return false;

        var encrypted = SecurityHelper.Encrypt(password);
        if (string.IsNullOrWhiteSpace(encrypted))
            return false;

        _currentSettings.ParentalLockPasswordEncrypted = encrypted;
        _currentSettings.ParentalLockUnlocked = true;
        SaveSettingsOnly();
        NotifyParentalStateChanged();
        return true;
    }

    private async Task<bool> UnlockParentalLockAsync(Window owner)
    {
        if (!HasParentalPassword)
            return true;

        var storedPassword = SecurityHelper.Decrypt(_currentSettings.ParentalLockPasswordEncrypted ?? string.Empty);
        if (string.IsNullOrWhiteSpace(storedPassword))
            return false;

        var password = await PromptForPasswordAsync(
            owner,
            T("Parental.Dialog.Title", "Parental Lock"),
            T("Parental.Dialog.EnterPasswordMessage", "Enter parental lock password."),
            requireConfirmation: false,
            validator: (input, _) =>
                string.Equals(input, storedPassword, StringComparison.Ordinal)
                    ? null
                    : T("Parental.Dialog.WrongPassword", "Wrong password."));

        if (string.IsNullOrWhiteSpace(password))
            return false;

        _currentSettings.ParentalLockUnlocked = true;
        SaveSettingsOnly();
        RefreshParentalFilteringState();
        return true;
    }

    private async Task<bool> EnsureParentalUnlockedForEditingAsync()
    {
        if (CurrentWindow is not { } owner)
            return false;

        if (!await EnsureParentalPasswordConfiguredAsync(owner))
            return false;

        if (IsParentalFilterActive && !await UnlockParentalLockAsync(owner))
            return false;

        return true;
    }

    private async Task ToggleParentalLockAsync()
    {
        if (CurrentWindow is not { } owner)
            return;

        if (!HasParentalPassword)
        {
            var configured = await EnsureParentalPasswordConfiguredAsync(owner);
            if (!configured)
                return;

            _currentSettings.ParentalLockUnlocked = false;
            SaveSettingsOnly();
            RefreshParentalFilteringState();
            return;
        }

        if (IsParentalFilterActive)
        {
            await UnlockParentalLockAsync(owner);
            return;
        }

        _currentSettings.ParentalLockUnlocked = false;
        SaveSettingsOnly();
        RefreshParentalFilteringState();
    }

    private async Task ChangeParentalPasswordAsync()
    {
        if (CurrentWindow is not { } owner)
            return;

        await ChangeParentalPasswordAsync(owner);
    }

    private async Task ChangeParentalPasswordAsync(Window owner)
    {
        if (owner == null)
            return;

        if (!HasParentalPassword)
        {
            await EnsureParentalPasswordConfiguredAsync(owner);
            return;
        }

        var storedPassword = SecurityHelper.Decrypt(_currentSettings.ParentalLockPasswordEncrypted ?? string.Empty);
        if (string.IsNullOrWhiteSpace(storedPassword))
            return;

        var currentPassword = await PromptForPasswordAsync(
            owner,
            T("Parental.Dialog.Title", "Parental Lock"),
            T("Parental.Dialog.ChangeCurrentMessage", "Enter current password."),
            requireConfirmation: false,
            validator: (input, _) =>
                string.Equals(input, storedPassword, StringComparison.Ordinal)
                    ? null
                    : T("Parental.Dialog.WrongPassword", "Wrong password."));

        if (string.IsNullOrWhiteSpace(currentPassword))
            return;

        var newPassword = await PromptForPasswordAsync(
            owner,
            T("Parental.Dialog.Title", "Parental Lock"),
            T("Parental.Dialog.ChangeNewMessage", "Enter new password."),
            requireConfirmation: true,
            validator: (input, _) =>
                string.Equals(input, storedPassword, StringComparison.Ordinal)
                    ? T("Parental.Validation.NewPasswordSameAsCurrent", "New password must be different.")
                    : null);

        if (string.IsNullOrWhiteSpace(newPassword))
            return;

        var encrypted = SecurityHelper.Encrypt(newPassword);
        if (string.IsNullOrWhiteSpace(encrypted))
            return;

        _currentSettings.ParentalLockPasswordEncrypted = encrypted;
        _currentSettings.ParentalLockUnlocked = true;
        SaveSettingsOnly();
        NotifyParentalStateChanged();
        RefreshParentalFilteringState(refreshCurrentContent: false);
    }

    private async Task ToggleItemProtectionAsync(MediaItem? item)
    {
        if (item == null)
            return;

        if (!await EnsureParentalUnlockedForEditingAsync())
            return;

        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(async () => await ToggleItemProtectionAsync(item), DispatcherPriority.Background);
            return;
        }

        var parentNode = FindParentNode(RootItems, item);

        _isApplyingProtectionChanges = true;
        try
        {
            item.IsProtected = !item.IsProtected;
            RefreshAncestorAutoProtectStates(parentNode);
        }
        finally
        {
            _isApplyingProtectionChanges = false;
        }

        FinalizeProtectionMutation();
    }

    private async Task ToggleNodeProtectionAsync(MediaNode? node)
    {
        if (node == null)
            return;

        if (!await EnsureParentalUnlockedForEditingAsync())
            return;

        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(async () => await ToggleNodeProtectionAsync(node), DispatcherPriority.Background);
            return;
        }

        var targetState = !IsNodeEffectivelyProtected(node);

        _isApplyingProtectionChanges = true;
        try
        {
            ApplyNodeProtectionRecursive(node, targetState);
            RefreshAncestorAutoProtectStates(node);
        }
        finally
        {
            _isApplyingProtectionChanges = false;
        }

        FinalizeProtectionMutation();
    }

    private async Task<string?> PromptForPasswordAsync(
        Window owner,
        string title,
        string message,
        bool requireConfirmation,
        PasswordPromptViewModel.PasswordValidator? validator = null)
    {
        var vm = new PasswordPromptViewModel(title, message, requireConfirmation, validator);
        var dialog = new PasswordPromptView { DataContext = vm };
        var result = await dialog.ShowDialog<bool>(owner);
        return result ? vm.Password : null;
    }
}
