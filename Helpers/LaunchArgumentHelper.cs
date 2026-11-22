using System;
using System.Text;

namespace Retromind.Helpers;

public static class LaunchArgumentHelper
{
    public static string CombineTemplateArguments(string? baseArgs, string? itemArgs)
    {
        baseArgs ??= string.Empty;
        itemArgs ??= string.Empty;

        if (string.IsNullOrWhiteSpace(itemArgs))
            return baseArgs;

        if (baseArgs.Contains("{file}", StringComparison.Ordinal) &&
            itemArgs.Contains("{file}", StringComparison.Ordinal))
        {
            return baseArgs.Replace("{file}", itemArgs, StringComparison.Ordinal);
        }

        return $"{baseArgs} {itemArgs}".Trim();
    }

    public static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = new StringBuilder(value.Length);
        var inQuotes = false;
        var pendingSpace = false;
        var backslashes = 0;

        foreach (var character in value)
        {
            if (!inQuotes && char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                backslashes = 0;
                continue;
            }

            if (pendingSpace)
                normalized.Append(' ');
            pendingSpace = false;
            normalized.Append(character);

            // ProcessStartInfo.Arguments treats a quote after an odd run of
            // backslashes as literal. Preserve the original escapes.
            if (character == '"' && backslashes % 2 == 0)
                inQuotes = !inQuotes;
            backslashes = character == '\\' ? backslashes + 1 : 0;
        }

        return normalized.ToString();
    }
}
