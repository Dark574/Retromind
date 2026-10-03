using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class MediaDataServiceSnapshotTests
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        IgnoreReadOnlyProperties = true
    };

    [Fact]
    public void CreateSnapshot_PreservesEveryPersistedPropertyAndDeepCopiesMutableData()
    {
        var roots = CreateFullyPopulatedLibrary();
        var originalNode = Assert.Single(roots);
        var originalItem = Assert.Single(originalNode.Items);

        AssertPersistenceContract(
            originalNode,
            nameof(MediaNode.NativeWrappersOverride),
            nameof(MediaNode.EnvironmentOverrides),
            nameof(MediaNode.SystemPreviewThemeId),
            nameof(MediaNode.Id),
            nameof(MediaNode.Name),
            nameof(MediaNode.Type),
            nameof(MediaNode.IsExpanded),
            nameof(MediaNode.RandomizeCovers),
            nameof(MediaNode.RandomizeMusic),
            nameof(MediaNode.AutoProtectNewChildren),
            nameof(MediaNode.DefaultEmulatorId),
            nameof(MediaNode.GameSystemId),
            nameof(MediaNode.StoreProviderId),
            nameof(MediaNode.ThemePath),
            nameof(MediaNode.Description),
            nameof(MediaNode.LogoFallbackEnabled),
            nameof(MediaNode.WallpaperFallbackEnabled),
            nameof(MediaNode.VideoFallbackEnabled),
            nameof(MediaNode.MarqueeFallbackEnabled),
            nameof(MediaNode.Assets),
            nameof(MediaNode.Children),
            nameof(MediaNode.Items));
        AssertPersistenceContract(
            originalItem,
            nameof(MediaItem.NativeWrappersOverride),
            nameof(MediaItem.EnvironmentOverrides),
            nameof(MediaItem.Id),
            nameof(MediaItem.Title),
            nameof(MediaItem.Files),
            nameof(MediaItem.MediaType),
            nameof(MediaItem.Description),
            nameof(MediaItem.Developer),
            nameof(MediaItem.Publisher),
            nameof(MediaItem.Platform),
            nameof(MediaItem.GameSystemId),
            nameof(MediaItem.RetroAchievementsGame),
            nameof(MediaItem.Source),
            nameof(MediaItem.Genre),
            nameof(MediaItem.Series),
            nameof(MediaItem.ReleaseType),
            nameof(MediaItem.SortTitle),
            nameof(MediaItem.PlayMode),
            nameof(MediaItem.MaxPlayers),
            nameof(MediaItem.ReleaseDate),
            nameof(MediaItem.Rating),
            nameof(MediaItem.Status),
            nameof(MediaItem.IsFavorite),
            nameof(MediaItem.IsProtected),
            nameof(MediaItem.Tags),
            nameof(MediaItem.CustomFields),
            nameof(MediaItem.GogDlcInstallations),
            nameof(MediaItem.Assets),
            nameof(MediaItem.EmulatorId),
            nameof(MediaItem.LauncherPath),
            nameof(MediaItem.LauncherArgs),
            nameof(MediaItem.WorkingDirectory),
            nameof(MediaItem.XdgConfigPath),
            nameof(MediaItem.XdgDataPath),
            nameof(MediaItem.XdgCachePath),
            nameof(MediaItem.XdgStatePath),
            nameof(MediaItem.XdgBasePath),
            nameof(MediaItem.PrefixPath),
            nameof(MediaItem.RunnerVersionId),
            nameof(MediaItem.OverrideWatchProcess),
            nameof(MediaItem.LastPlayed),
            nameof(MediaItem.PlayCount),
            nameof(MediaItem.TotalPlayTime));
        AssertPersistenceContract(
            Assert.Single(originalNode.Assets),
            nameof(MediaAsset.Id),
            nameof(MediaAsset.Type),
            nameof(MediaAsset.RelativePath));
        AssertPersistenceContract(
            Assert.Single(originalItem.Files),
            nameof(MediaFileRef.Kind),
            nameof(MediaFileRef.Path),
            nameof(MediaFileRef.Label),
            nameof(MediaFileRef.Index));
        AssertPersistenceContract(
            Assert.Single(originalItem.NativeWrappersOverride!),
            nameof(LaunchWrapper.Path),
            nameof(LaunchWrapper.Args));
        AssertPersistenceContract(
            originalItem.RetroAchievementsGame!,
            nameof(RetroAchievementsGameIdentity.GameId),
            nameof(RetroAchievementsGameIdentity.ConsoleId),
            nameof(RetroAchievementsGameIdentity.GameSystemId),
            nameof(RetroAchievementsGameIdentity.Hash),
            nameof(RetroAchievementsGameIdentity.Title));
        AssertPersistenceContract(
            Assert.Single(originalItem.GogDlcInstallations!),
            nameof(GogDlcInstallationState.ProductId),
            nameof(GogDlcInstallationState.Title),
            nameof(GogDlcInstallationState.Platform),
            nameof(GogDlcInstallationState.InstalledVersion),
            nameof(GogDlcInstallationState.InstalledInstallerSignature));

        var service = new MediaDataService();
        var snapshot = service.CreateSnapshot(roots);
        var expectedJson = service.Serialize(roots);
        var snapshotJson = service.Serialize(snapshot);

        AssertJsonEqual(expectedJson, snapshotJson);

        MutateEveryMutableBranch(originalNode, originalItem);

        Assert.Equal(snapshotJson, service.Serialize(snapshot));
        Assert.NotEqual(expectedJson, service.Serialize(roots));
    }

    private static ObservableCollection<MediaNode> CreateFullyPopulatedLibrary()
    {
        var item = new MediaItem
        {
            NativeWrappersOverride =
            [
                new LaunchWrapper { Path = "item-wrapper", Args = "--item {file}" }
            ],
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["ITEM_ENV"] = "item-value"
            },
            Id = "item-id",
            Title = "Item title",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.LibraryRelative,
                    Path = "Library/Games/Game/game.rom",
                    Label = "Disc 1",
                    Index = 1
                }
            ],
            MediaType = MediaType.Emulator,
            Description = "Item description",
            Developer = "Developer",
            Publisher = "Publisher",
            Platform = "Platform",
            GameSystemId = "nintendo.snes",
            RetroAchievementsGame = new RetroAchievementsGameIdentity
            {
                GameId = 123,
                ConsoleId = 5,
                GameSystemId = "nintendo.snes",
                Hash = "hash",
                Title = "Achievement title"
            },
            Source = "Source",
            Genre = "Genre",
            Series = "Series",
            ReleaseType = "Release type",
            SortTitle = "Sort title",
            PlayMode = "Co-op",
            MaxPlayers = "2",
            ReleaseDate = new DateTime(1994, 11, 21, 0, 0, 0, DateTimeKind.Utc),
            Rating = 91.5,
            Status = PlayStatus.Completed,
            IsFavorite = true,
            IsProtected = true,
            Tags = new ObservableCollection<string> { "tag" },
            CustomFields = new Dictionary<string, string>
            {
                ["custom"] = "value"
            },
            GogDlcInstallations =
            [
                new GogDlcInstallationState
                {
                    ProductId = "dlc-id",
                    Title = "DLC",
                    Platform = "windows",
                    InstalledVersion = "1.2.3",
                    InstalledInstallerSignature = "signature"
                }
            ],
            Assets = new ObservableCollection<MediaAsset>
            {
                new()
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Type = AssetType.Logo,
                    RelativePath = "Library/Games/Game/Logo/logo.png"
                }
            },
            EmulatorId = "emulator-id",
            LauncherPath = "Library/Launchers/launcher",
            LauncherArgs = "--launch {file}",
            WorkingDirectory = "Library/Games/Game",
            XdgConfigPath = "Library/Xdg/config",
            XdgDataPath = "Library/Xdg/data",
            XdgCachePath = "Library/Xdg/cache",
            XdgStatePath = "Library/Xdg/state",
            XdgBasePath = "Library/Xdg",
            PrefixPath = "Prefixes/Game",
            RunnerVersionId = "runner-id",
            OverrideWatchProcess = "game-process",
            LastPlayed = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            PlayCount = 7,
            TotalPlayTime = TimeSpan.FromHours(12)
        };

        var node = new MediaNode
        {
            NativeWrappersOverride =
            [
                new LaunchWrapper { Path = "node-wrapper", Args = "--node {file}" }
            ],
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["NODE_ENV"] = "node-value"
            },
            SystemPreviewThemeId = "SNES",
            Id = "node-id",
            Name = "Node name",
            Type = NodeType.Group,
            IsExpanded = true,
            RandomizeCovers = true,
            RandomizeMusic = false,
            AutoProtectNewChildren = true,
            DefaultEmulatorId = "default-emulator-id",
            GameSystemId = "nintendo.snes",
            StoreProviderId = "gog",
            ThemePath = "Themes/Default/theme.axaml",
            Description = "Node description",
            LogoFallbackEnabled = true,
            WallpaperFallbackEnabled = true,
            VideoFallbackEnabled = true,
            MarqueeFallbackEnabled = true,
            Assets = new ObservableCollection<MediaAsset>
            {
                new()
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Type = AssetType.Cover,
                    RelativePath = "Library/Node/Cover/cover.png"
                }
            },
            Children = new ObservableCollection<MediaNode>
            {
                new() { Id = "child-id", Name = "Child" }
            },
            Items = new ObservableCollection<MediaItem> { item }
        };

        return new ObservableCollection<MediaNode> { node };
    }

    private static void AssertPersistenceContract<T>(T populated, params string[] expectedProperties)
        where T : class, new()
    {
        var actualProperties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(IsPersistedProperty)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var expectedNames = expectedProperties
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedNames, actualProperties.Select(property => property.Name));

        var defaults = new T();
        foreach (var property in actualProperties)
        {
            var populatedValue = property.GetValue(populated);
            Assert.True(
                populatedValue != null,
                $"The snapshot fixture must populate {typeof(T).Name}.{property.Name}.");

            var populatedNode = JsonSerializer.SerializeToNode(
                populatedValue,
                property.PropertyType,
                ContractJsonOptions);
            var defaultNode = JsonSerializer.SerializeToNode(
                property.GetValue(defaults),
                property.PropertyType,
                ContractJsonOptions);

            Assert.False(
                JsonNode.DeepEquals(populatedNode, defaultNode),
                $"The snapshot fixture must use a non-default value for {typeof(T).Name}.{property.Name}.");
        }
    }

    private static bool IsPersistedProperty(PropertyInfo property)
    {
        if (property.GetMethod is not { IsPublic: true } ||
            property.SetMethod is not { IsPublic: true })
        {
            return false;
        }

        var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
        return jsonIgnore == null || jsonIgnore.Condition != JsonIgnoreCondition.Always;
    }

    private static void MutateEveryMutableBranch(MediaNode node, MediaItem item)
    {
        node.NativeWrappersOverride![0].Args = "changed node wrapper";
        node.EnvironmentOverrides!["NODE_ENV"] = "changed";
        node.Assets[0].RelativePath = "changed/node-asset.png";
        node.Children[0].Name = "Changed child";
        node.Items.Add(new MediaItem("Added item"));

        item.NativeWrappersOverride![0].Args = "changed item wrapper";
        item.EnvironmentOverrides["ITEM_ENV"] = "changed";
        item.Files[0].Path = "changed/game.rom";
        item.RetroAchievementsGame!.Title = "Changed achievement title";
        item.Tags[0] = "changed tag";
        item.CustomFields["custom"] = "changed";
        item.GogDlcInstallations![0].Title = "Changed DLC";
        item.Assets[0].RelativePath = "changed/item-asset.png";
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode));
    }
}
