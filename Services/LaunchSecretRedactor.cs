using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Retromind.Services;

internal static partial class LaunchSecretRedactor
{
    internal static string? Redact(
        string? value,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        value = RedactArguments(value);
        if (string.IsNullOrWhiteSpace(value) || environment == null)
            return value;

        foreach (var secret in environment
                     .Where(pair => IsSensitiveName(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                     .Select(pair => pair.Value)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(secret => secret.Length))
        {
            value = value.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        return value;
    }

    internal static string? RedactArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return arguments;

        return SensitiveArgumentRegex().Replace(
            arguments,
            match => match.Groups["prefix"].Value + "<redacted>");
    }

    internal static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("PASSWD", StringComparison.Ordinal) ||
               normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("APIKEY", StringComparison.Ordinal) ||
               normalized.Contains("AUTH", StringComparison.Ordinal) ||
               normalized.Contains("COOKIE", StringComparison.Ordinal) ||
               normalized.Contains("CREDENTIAL", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        "(?<prefix>(?:--?|/)?(?:auth[-_]?password|auth[-_]?login|password|passwd|token|secret|api[-_]?key|authorization|cookie|credential)(?:\\s*=\\s*|\\s+))(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s&#]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveArgumentRegex();
}
