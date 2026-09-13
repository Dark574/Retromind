using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.Helpers;

public static class ParentalControlHelper
{
    public static bool IsFilterActive(AppSettings settings)
    {
        if (settings == null)
            return false;

        return !string.IsNullOrWhiteSpace(settings.ParentalLockPasswordEncrypted) &&
               !settings.ParentalLockUnlocked;
    }

    public static bool AreAllItemsProtectedInSubtree(MediaNode node, out bool hasAnyItems)
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

    public static bool IsNodeEffectivelyProtected(MediaNode node)
    {
        var allProtected = AreAllItemsProtectedInSubtree(node, out var hasAnyItems);
        return hasAnyItems ? allProtected : node.AutoProtectNewChildren;
    }

    public static void ApplyNodeProtectionRecursive(MediaNode node, bool isProtected)
    {
        node.AutoProtectNewChildren = isProtected;

        foreach (var item in node.Items)
            item.IsProtected = isProtected;

        foreach (var child in node.Children)
            ApplyNodeProtectionRecursive(child, isProtected);
    }

    public static void RecalculateAutoProtectStates(IEnumerable<MediaNode> roots)
    {
        foreach (var root in roots)
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
}
