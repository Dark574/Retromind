using System;

namespace Retromind.Helpers;

public static class CustomFieldKeyHelper
{
    private const string InternalStorePrefix = "Store.";

    public static bool IsInternal(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
               key.Trim().StartsWith(InternalStorePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
