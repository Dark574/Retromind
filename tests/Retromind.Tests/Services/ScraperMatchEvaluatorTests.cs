using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScraperMatchEvaluatorTests
{
    [Theory]
    [InlineData("PlayStation", "Sony PlayStation")]
    [InlineData("PS1", "PlayStation 1")]
    [InlineData("PC", "PC (Microsoft Windows)")]
    [InlineData("Mega Drive", "Genesis")]
    [InlineData("PC Engine", "TurboGrafx-16")]
    [InlineData("GBA", "Nintendo Game Boy Advance")]
    [InlineData("MAME", "Arcade (MAME)")]
    public void SelectBestMatch_UnambiguousPlatformAliasesReceiveBonus(
        string itemPlatform,
        string resultPlatform)
    {
        var decision = Evaluate(itemPlatform, resultPlatform);

        Assert.Equal(ScraperMatchStatus.Match, decision.Status);
        Assert.Equal(1.04, decision.Score, precision: 10);
    }

    [Theory]
    [InlineData("PlayStation", "PlayStation 2")]
    [InlineData("PC", "PC Engine")]
    [InlineData("Nintendo DS", "Nintendo Switch")]
    [InlineData("Game Boy", "Game Boy Color")]
    [InlineData("Xbox", "Xbox 360")]
    [InlineData("Neo Geo", "Neo Geo CD")]
    public void SelectBestMatch_RelatedButDifferentPlatformsDoNotReceiveBonus(
        string itemPlatform,
        string resultPlatform)
    {
        var decision = Evaluate(itemPlatform, resultPlatform);

        Assert.Equal(ScraperMatchStatus.Match, decision.Status);
        Assert.Equal(1.0, decision.Score, precision: 10);
    }

    [Fact]
    public void SelectBestMatch_MultiPlatformResultMatchesAnExactAlternative()
    {
        var decision = Evaluate("Linux", "PC (Microsoft Windows), Linux");

        Assert.Equal(1.04, decision.Score, precision: 10);
    }

    private static ScraperMatchDecision Evaluate(string itemPlatform, string resultPlatform)
    {
        return ScraperMatchEvaluator.SelectBestMatch(
            "Test Game",
            [new ScraperSearchResult { Title = "Test Game", Platform = resultPlatform }],
            itemPlatform);
    }
}
