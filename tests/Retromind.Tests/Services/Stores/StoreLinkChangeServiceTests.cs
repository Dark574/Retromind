using System.IO;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores;

namespace Retromind.Tests.Services.Stores;

public sealed class StoreLinkChangeServiceTests
{
    [Fact]
    public async Task GogToEpicToGog_PreservesUserDataMediaLaunchConfigurationAndStatistics()
    {
        var lastPlayed = new DateTime(2026, 10, 15, 18, 30, 0, DateTimeKind.Utc);
        var item = new MediaItem("Example Game")
        {
            Description = "User description",
            Developer = "Developer",
            PlayCount = 7,
            TotalPlayTime = TimeSpan.FromHours(12),
            LastPlayed = lastPlayed,
            LauncherPath = "umu-run",
            LauncherArgs = "{file}",
            PrefixPath = "Prefixes/example",
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["User.Note"] = "keep me",
                [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Gog,
                [CustomFieldKeyHelper.StoreGameId] = "gog-game",
                ["Store.Obsolete"] = "remove me"
            }
        };
        var cover = new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = "Library/Media/example-cover.png"
        };
        item.Assets.Add(cover);
        var originalAssets = item.Assets;

        await ApplyAsync(item, StoreProviderIds.Epic, "epic-game");
        await ApplyAsync(item, StoreProviderIds.Gog, "new-gog-game");

        Assert.Equal(StoreProviderIds.Gog, item.CustomFields[CustomFieldKeyHelper.StoreProviderId]);
        Assert.Equal("new-gog-game", item.CustomFields[CustomFieldKeyHelper.StoreGameId]);
        Assert.Equal("keep me", item.CustomFields["User.Note"]);
        Assert.False(item.CustomFields.ContainsKey("Store.Obsolete"));
        Assert.Equal("User description", item.Description);
        Assert.Equal("Developer", item.Developer);
        Assert.Same(originalAssets, item.Assets);
        Assert.Same(cover, Assert.Single(item.Assets));
        Assert.Equal("umu-run", item.LauncherPath);
        Assert.Equal("{file}", item.LauncherArgs);
        Assert.Equal("Prefixes/example", item.PrefixPath);
        Assert.Equal(7, item.PlayCount);
        Assert.Equal(TimeSpan.FromHours(12), item.TotalPlayTime);
        Assert.Equal(lastPlayed, item.LastPlayed);
    }

    [Fact]
    public void StoreChange_WithManagedInstallation_IsBlocked()
    {
        var item = CreateLinkedItem(StoreProviderIds.Gog, "gog-game");
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = "Library/Games/GOG/Example";

        var reason = StoreLinkChangeService.GetBlockReason(item, StoreProviderIds.Epic);

        Assert.Equal(StoreLinkChangeBlockReason.ManagedInstallation, reason);
    }

    [Fact]
    public async Task ApplyAndSaveAsync_WhenSavingFails_RestoresTheCompleteStoreState()
    {
        var item = CreateLinkedItem(StoreProviderIds.Gog, "gog-game");
        item.CustomFields["User.Note"] = "keep me";
        var previousFields = item.CustomFields;
        var previousDlcState = new List<GogDlcInstallationState>
        {
            new() { ProductId = "dlc-1", Title = "DLC" }
        };
        item.GogDlcInstallations = previousDlcState;
        var updatedFields = StoreLinkChangeService.BuildChangedFields(
            item.CustomFields,
            StoreProviderIds.Epic,
            "epic-game");
        var dirtyCalls = 0;

        await Assert.ThrowsAsync<IOException>(() =>
            StoreLinkChangeService.ApplyAndSaveAsync(
                item,
                updatedFields,
                () => Task.FromResult(false),
                () => dirtyCalls++,
                "Save failed."));

        Assert.Same(previousFields, item.CustomFields);
        Assert.Same(previousDlcState, item.GogDlcInstallations);
        Assert.Equal(StoreProviderIds.Gog, item.CustomFields[CustomFieldKeyHelper.StoreProviderId]);
        Assert.Equal("gog-game", item.CustomFields[CustomFieldKeyHelper.StoreGameId]);
        Assert.Equal("keep me", item.CustomFields["User.Note"]);
        Assert.Equal(2, dirtyCalls);
    }

    [Fact]
    public void BuildChangedFields_RemovesAllStoreFieldsButPreservesUserFields()
    {
        var previousFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["User.Note"] = "keep me",
            ["AnotherField"] = "also keep me",
            [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Gog,
            [CustomFieldKeyHelper.StoreGameId] = "gog-game",
            [CustomFieldKeyHelper.StoreInstallPath] = "old/path",
            ["STORE.FutureField"] = "old internal value"
        };

        var changedFields = StoreLinkChangeService.BuildChangedFields(
            previousFields,
            StoreProviderIds.Epic,
            "epic-game");

        Assert.Equal(4, changedFields.Count);
        Assert.Equal("keep me", changedFields["User.Note"]);
        Assert.Equal("also keep me", changedFields["AnotherField"]);
        Assert.Equal(StoreProviderIds.Epic, changedFields[CustomFieldKeyHelper.StoreProviderId]);
        Assert.Equal("epic-game", changedFields[CustomFieldKeyHelper.StoreGameId]);
        Assert.False(changedFields.ContainsKey(CustomFieldKeyHelper.StoreInstallPath));
        Assert.False(changedFields.ContainsKey("STORE.FutureField"));
    }

    private static async Task ApplyAsync(MediaItem item, string providerId, string gameId)
    {
        var updatedFields = StoreLinkChangeService.BuildChangedFields(
            item.CustomFields,
            providerId,
            gameId);
        await StoreLinkChangeService.ApplyAndSaveAsync(
            item,
            updatedFields,
            () => Task.FromResult(true),
            () => { },
            "Save failed.");
    }

    private static MediaItem CreateLinkedItem(string providerId, string gameId) => new("Example Game")
    {
        CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomFieldKeyHelper.StoreProviderId] = providerId,
            [CustomFieldKeyHelper.StoreGameId] = gameId
        }
    };
}
