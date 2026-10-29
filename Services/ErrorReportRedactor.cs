using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>Redacts before serialization, including exception chains and every metadata field.</summary>
internal sealed partial class ErrorReportRedactor
{
    private const string Replacement = "<redacted>";
    private readonly string[] _knownSecrets;

    internal ErrorReportRedactor(ErrorReport error)
    {
        _knownSecrets = (error.SensitiveValues ?? Array.Empty<string>())
            .Concat(error.Context?.Where(pair => IsSensitiveName(pair.Key)).Select(pair => pair.Value)
                    ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    private static bool IsSensitiveName(string name) =>
        LaunchSecretRedactor.IsSensitiveName(name) ||
        name.Equals("code", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("sid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwd", StringComparison.OrdinalIgnoreCase);

    internal string? Redact(string? text)
    {
        if (text == null)
            return null;

        foreach (var secret in _knownSecrets)
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);

        // Handle HTTP authentication and URL userinfo before key/value replacement.
        text = AuthenticationRegex().Replace(text, "$1" + Replacement);
        text = UrlCredentialsRegex().Replace(text, "$1" + Replacement + "@");
        text = SensitiveHeaderRegex().Replace(text, "$1" + Replacement);
        text = AssignmentRegex().Replace(text, match =>
            IsSensitiveName(match.Groups["key"].Value)
                ? match.Groups["prefix"].Value + Replacement
                : match.Value);

        // Reuse the launch redactor for command-line credentials and sensitive-name policy.
        return LaunchSecretRedactor.RedactArguments(text);
    }

    internal Dictionary<string, string?>? RedactContext(IReadOnlyDictionary<string, string>? context)
    {
        if (context == null)
            return null;

        var result = new Dictionary<string, string?>();
        foreach (var pair in context)
            result[Redact(pair.Key)!] = IsSensitiveName(pair.Key) ? Replacement : Redact(pair.Value);
        return result;
    }

    [GeneratedRegex(@"\b(Bearer\s+|Basic\s+)[^\s,\""'};]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthenticationRegex();

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlCredentialsRegex();

    [GeneratedRegex(@"\b((?:Authorization|Proxy-Authorization|Cookie|Set-Cookie)\s*:\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex("(?<prefix>[\"']?(?<key>[a-zA-Z0-9_-]*(?:password|passwd|pwd|token|secret|api[-_]?key|auth|cookie|credential|sid|code)[a-zA-Z0-9_-]*)[\"']?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|'[^']*'|[^\\s,;&}\\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AssignmentRegex();
}
