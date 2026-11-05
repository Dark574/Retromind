using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Converts API-provided display text to plain characters at the provider boundary.
/// Decode once while mapping raw fields, never when copying results or applying them to the library.
/// IDs, URLs and protocol values are not display text and must not pass through this helper.
/// </summary>
internal static class ScraperText
{
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Decode(string? value) => WebUtility.HtmlDecode(value);
}
