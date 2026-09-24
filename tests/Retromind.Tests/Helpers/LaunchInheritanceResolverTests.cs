using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class LaunchInheritanceResolverTests
{
    [Fact]
    public void ResolveNativeWrappers_MergesNearestNodeBeforeEmulator()
    {
        var emulatorWrapper = new LaunchWrapper { Path = "emulator-wrapper" };
        var nodeWrapper = new LaunchWrapper { Path = "node-wrapper" };
        var emulator = new EmulatorConfig { NativeWrappersOverride = [emulatorWrapper] };
        var parent = new MediaNode("Parent", NodeType.Group)
        {
            NativeWrappersOverride = [nodeWrapper]
        };
        var leaf = new MediaNode("Leaf", NodeType.Group);
        parent.Children.Add(leaf);
        var roots = new ObservableCollection<MediaNode> { parent };

        var result = LaunchInheritanceResolver.ResolveNativeWrappers(
            emulator,
            leaf,
            roots,
            itemOverride: null);

        Assert.Equal([nodeWrapper, emulatorWrapper], result);
    }

    [Fact]
    public void ResolveNativeWrappers_EmptyNodeOverrideKeepsEmulatorBase()
    {
        var emulatorWrapper = new LaunchWrapper { Path = "emulator-wrapper" };
        var emulator = new EmulatorConfig { NativeWrappersOverride = [emulatorWrapper] };
        var root = new MediaNode("Root", NodeType.Group)
        {
            NativeWrappersOverride = [new LaunchWrapper { Path = "root-wrapper" }]
        };
        var leaf = new MediaNode("Leaf", NodeType.Group)
        {
            NativeWrappersOverride = []
        };
        root.Children.Add(leaf);
        var roots = new ObservableCollection<MediaNode> { root };

        var result = LaunchInheritanceResolver.ResolveNativeWrappers(
            emulator,
            leaf,
            roots,
            itemOverride: null);

        Assert.Equal([emulatorWrapper], result);
    }

    [Fact]
    public void ResolveNativeWrappers_ItemOverrideWinsIncludingEmptyList()
    {
        var emulator = new EmulatorConfig
        {
            NativeWrappersOverride = [new LaunchWrapper { Path = "emulator-wrapper" }]
        };
        var root = new MediaNode("Root", NodeType.Group)
        {
            NativeWrappersOverride = [new LaunchWrapper { Path = "node-wrapper" }]
        };
        var roots = new ObservableCollection<MediaNode> { root };

        var result = LaunchInheritanceResolver.ResolveNativeWrappers(
            emulator,
            root,
            roots,
            itemOverride: []);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveEnvironmentOverrides_AppliesLayersInLaunchOrder()
    {
        var settings = new AppSettings
        {
            RunnerVersions =
            [
                new RunnerVersionConfig
                {
                    Id = "emulator-runner",
                    Kind = RunnerVersionKind.Proton,
                    Path = "/runner/emulator"
                },
                new RunnerVersionConfig
                {
                    Id = "item-runner",
                    Kind = RunnerVersionKind.Proton,
                    Path = "/runner/item"
                }
            ]
        };
        var emulator = new EmulatorConfig
        {
            DefaultRunnerVersionId = "emulator-runner",
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["SHARED"] = "emulator",
                ["EMULATOR_ONLY"] = "yes"
            }
        };
        var root = new MediaNode("Root", NodeType.Group)
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["SHARED"] = "node",
                ["NODE_ONLY"] = "yes"
            }
        };
        var leaf = new MediaNode("Leaf", NodeType.Group);
        root.Children.Add(leaf);
        var roots = new ObservableCollection<MediaNode> { root };
        var itemOverrides = new Dictionary<string, string>
        {
            ["SHARED"] = "item",
            ["ITEM_ONLY"] = "yes"
        };

        var result = LaunchInheritanceResolver.ResolveEnvironmentOverrides(
            settings,
            emulator,
            leaf,
            roots,
            itemOverrides,
            itemRunnerVersionId: "item-runner");

        Assert.Equal("item", result["SHARED"]);
        Assert.Equal("yes", result["EMULATOR_ONLY"]);
        Assert.Equal("yes", result["NODE_ONLY"]);
        Assert.Equal("yes", result["ITEM_ONLY"]);
        Assert.Equal("/runner/item", result["PROTONPATH"]);
    }

    [Fact]
    public void ResolveEnvironmentOverrides_EmptyNearestNodeStopsAncestorInheritance()
    {
        var settings = new AppSettings();
        var emulator = new EmulatorConfig
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["EMULATOR_ONLY"] = "yes"
            }
        };
        var root = new MediaNode("Root", NodeType.Group)
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["ROOT_ONLY"] = "yes"
            }
        };
        var leaf = new MediaNode("Leaf", NodeType.Group)
        {
            EnvironmentOverrides = new Dictionary<string, string>()
        };
        root.Children.Add(leaf);
        var roots = new ObservableCollection<MediaNode> { root };

        var result = LaunchInheritanceResolver.ResolveEnvironmentOverrides(
            settings,
            emulator,
            leaf,
            roots,
            itemOverrides: null,
            itemRunnerVersionId: null);

        Assert.Equal("yes", result["EMULATOR_ONLY"]);
        Assert.DoesNotContain("ROOT_ONLY", result);
    }
}
