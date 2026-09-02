using System.Text.Json;
using Retromind.Models;

namespace Retromind.Tests.Models;

public sealed class ScraperConfigTests
{
    [Fact]
    public void TypeChange_UpdatesAutomaticallyGeneratedName()
    {
        var config = new ScraperConfig { Type = ScraperType.IGDB };

        config.Type = ScraperType.TMDB;

        Assert.Equal("TMDB", config.Name);
        Assert.False(config.IsNameCustomized);
    }

    [Fact]
    public void TypeChange_PreservesCustomizedName()
    {
        var config = new ScraperConfig
        {
            Type = ScraperType.IGDB,
            Name = "My games scraper"
        };

        config.Type = ScraperType.TMDB;

        Assert.Equal("My games scraper", config.Name);
        Assert.True(config.IsNameCustomized);
    }

    [Fact]
    public void Serialization_PreservesAutomaticNameState()
    {
        var original = new ScraperConfig { Type = ScraperType.IGDB };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ScraperConfig>(json)!;
        restored.Type = ScraperType.TMDB;

        Assert.Equal("TMDB", restored.Name);
        Assert.False(restored.IsNameCustomized);
    }

    [Fact]
    public void LegacyAutomaticName_IsRecognizedWithoutPersistedState()
    {
        const string json = """
            { "Name": "IGDB", "Type": 2 }
            """;

        var restored = JsonSerializer.Deserialize<ScraperConfig>(json)!;
        restored.Type = ScraperType.TMDB;

        Assert.Equal("TMDB", restored.Name);
        Assert.False(restored.IsNameCustomized);
    }

    [Fact]
    public void LegacyCustomizedName_RemainsCustomized()
    {
        const string json = """
            { "Name": "My games scraper", "Type": 2 }
            """;

        var restored = JsonSerializer.Deserialize<ScraperConfig>(json)!;
        restored.Type = ScraperType.TMDB;

        Assert.Equal("My games scraper", restored.Name);
        Assert.True(restored.IsNameCustomized);
    }
}
