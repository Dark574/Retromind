using System;
using System.Text.RegularExpressions;

namespace Retromind.Helpers;

public static class MultiDiscFileNameHelper
{
    // Matches: " (Disk 1)", "_Disk1", "_Side_1", " - CD 1", "_Scen", etc.
    // A clear separator before the token avoids matches inside words such as "Unterirdische".
    private static readonly Regex MultiDiscRegex = new(
        @"(?:^|[\s_\-]|\(|\[)\s*(?:(?<kind>Disk|Disc|CD|Side|Part)[\s_\-]*(?<token>[0-9A-H]+)|(?<standalone>Scen)(?=$|[\s_\-\)\]]))(?:\s*(?:\)|\]))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GroupingSeparatorRegex = new(
        @"[\s_]+",
        RegexOptions.Compiled);

    public static (string CleanTitle, int? Index, string? Label) Parse(string fileNameWithoutExtension)
    {
        var cleanTitle = fileNameWithoutExtension.Trim();
        var match = MultiDiscRegex.Match(fileNameWithoutExtension);
        if (!match.Success)
            return (cleanTitle, null, null);

        cleanTitle = fileNameWithoutExtension.Replace(match.Value, "").Trim();

        var standaloneLabel = match.Groups["standalone"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(standaloneLabel))
            return (cleanTitle, null, standaloneLabel);

        var kind = match.Groups["kind"].Value.Trim();
        var token = match.Groups["token"].Value.Trim();
        var index = ParseIndex(token);

        return (cleanTitle, index, BuildLabel(kind, token, index));
    }

    public static string GetGroupingKey(string cleanTitle)
    {
        return GroupingSeparatorRegex.Replace(cleanTitle.Trim(), " ");
    }

    private static int? ParseIndex(string token)
    {
        if (int.TryParse(token, out var number) && number > 0)
            return number;

        if (token.Length == 1)
        {
            var side = char.ToUpperInvariant(token[0]);
            if (side is >= 'A' and <= 'H')
                return side - 'A' + 1;
        }

        return null;
    }

    private static string? BuildLabel(string kind, string token, int? index)
    {
        if (string.Equals(kind, "Side", StringComparison.OrdinalIgnoreCase) && token.Length == 1)
        {
            var side = char.ToUpperInvariant(token[0]);
            if (side is >= 'A' and <= 'H')
                return $"Side {side}";
        }

        if (!string.IsNullOrWhiteSpace(token))
            return $"{kind} {token}";

        return index.HasValue ? $"{kind} {index.Value}" : null;
    }
}
