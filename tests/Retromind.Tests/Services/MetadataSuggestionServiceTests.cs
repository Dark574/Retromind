using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class MetadataSuggestionServiceTests
{
    [Fact]
    public void CustomFields_UseOnlyTheSelectedNodeAndIgnoreStoreMetadata()
    {
        var games = new MediaNode { Name = "Games" };
        games.Items.Add(CreateItem(
            ("Completeness", "Complete"),
            ("Perspective", "First-person"),
            ("Store.GameId", "123")));
        games.Items.Add(CreateItem(
            ("completeness", "Unplayed"),
            ("Perspective", "Isometric")));

        var books = new MediaNode { Name = "Books" };
        books.Items.Add(CreateItem(("Binding", "Hardcover")));

        var service = new MetadataSuggestionService([games, books], games);

        Assert.Equal(["Completeness", "Perspective"], service.GetKnownCustomFieldKeys());
        Assert.Equal("Completeness", service.GetBestCustomFieldKeyMatch("comp"));
        Assert.Null(service.GetBestCustomFieldKeyMatch("bind"));
    }

    [Fact]
    public void CustomFieldValues_AreSuggestedForTheirMatchingKeyOnly()
    {
        var games = new MediaNode { Name = "Games" };
        games.Items.Add(CreateItem(
            ("Completeness", "Complete"),
            ("Perspective", "First-person")));
        games.Items.Add(CreateItem(
            ("Completeness", "Unplayed"),
            ("Perspective", "Isometric")));

        var service = new MetadataSuggestionService([games], games);

        Assert.Equal("Complete", service.GetBestCustomFieldValueMatch("completeness", "comp"));
        Assert.Equal("Unplayed", service.GetBestCustomFieldValueMatch("Completeness", "unp"));
        Assert.Null(service.GetBestCustomFieldValueMatch("Perspective", "comp"));
        Assert.Null(service.GetBestCustomFieldValueMatch("Store.GameId", "1"));
    }

    private static MediaItem CreateItem(params (string Key, string Value)[] fields)
    {
        var item = new MediaItem();
        foreach (var (key, value) in fields)
            item.CustomFields[key] = value;

        return item;
    }
}
