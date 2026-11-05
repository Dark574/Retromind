namespace Retromind.Models;

/// <summary>
/// An individually selectable document returned by a scraper. URLs are transient;
/// only the imported local file is persisted in the library.
/// </summary>
public sealed record ScraperDocument
{
    public string Url { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Region { get; init; }
    public string Format { get; init; } = "pdf";
    public int Number { get; init; }

    public string GetDisplayLabel(string fallbackTitle)
    {
        var title = string.IsNullOrWhiteSpace(Title)
            ? (Number > 0 ? $"{fallbackTitle} {Number}" : fallbackTitle)
            : Title.Trim();
        var details = string.IsNullOrWhiteSpace(Region)
            ? Format.ToUpperInvariant()
            : $"{Region.ToUpperInvariant()}, {Format.ToUpperInvariant()}";
        return $"{title} ({details})";
    }
}
