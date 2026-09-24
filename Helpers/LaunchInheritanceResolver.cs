using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Resolves the shared emulator -> node -> item inheritance rules used by
/// launch execution and the media editor preview.
/// </summary>
public static class LaunchInheritanceResolver
{
    public static List<LaunchWrapper> ResolveNativeWrappers(
        EmulatorConfig? emulator,
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes,
        IReadOnlyList<LaunchWrapper>? itemOverride,
        bool matchNodesById = false)
    {
        if (itemOverride != null)
            return itemOverride.ToList();

        var emulatorWrappers = emulator?.NativeWrappersOverride ?? [];
        var nodeOverride = FindNearestNativeWrapperOverrideNode(
            parentNode,
            rootNodes,
            matchNodesById);
        var nodeWrappers = nodeOverride?.NativeWrappersOverride;

        if (nodeWrappers is not { Count: > 0 })
            return emulatorWrappers.ToList();

        var result = new List<LaunchWrapper>(nodeWrappers.Count + emulatorWrappers.Count);
        result.AddRange(nodeWrappers);
        result.AddRange(emulatorWrappers);
        return result;
    }

    public static Dictionary<string, string> ResolveEnvironmentOverrides(
        AppSettings settings,
        EmulatorConfig? emulator,
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes,
        IReadOnlyDictionary<string, string>? itemOverrides,
        string? itemRunnerVersionId,
        bool matchNodesById = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddEnvironmentOverrides(result, emulator?.EnvironmentOverrides);

        RunnerVersionEnvironmentHelper.ApplyRunnerToEnvironment(
            result,
            settings,
            emulator,
            emulator?.DefaultRunnerVersionId);

        var nodeOverride = FindNearestEnvironmentOverrideNode(
            parentNode,
            rootNodes,
            matchNodesById);
        AddEnvironmentOverrides(result, nodeOverride?.EnvironmentOverrides);
        AddEnvironmentOverrides(result, itemOverrides);

        RunnerVersionEnvironmentHelper.ApplyRunnerToEnvironment(
            result,
            settings,
            emulator,
            itemRunnerVersionId);

        return result;
    }

    public static MediaNode? FindNearestNativeWrapperOverrideNode(
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes,
        bool matchNodesById = false)
        => FindNearestOverrideNode(
            parentNode,
            rootNodes,
            static node => node.NativeWrappersOverride != null,
            matchNodesById);

    public static MediaNode? FindNearestEnvironmentOverrideNode(
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes,
        bool matchNodesById = false)
        => FindNearestOverrideNode(
            parentNode,
            rootNodes,
            static node => node.EnvironmentOverrides != null,
            matchNodesById);

    private static MediaNode? FindNearestOverrideNode(
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes,
        Func<MediaNode, bool> hasOverride,
        bool matchNodesById)
    {
        if (parentNode == null || rootNodes.Count == 0)
            return null;

        var chain = PathHelper.GetNodeChain(parentNode, rootNodes, matchNodesById);
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            if (hasOverride(chain[index]))
                return chain[index];
        }

        return null;
    }

    private static void AddEnvironmentOverrides(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
            return;

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            target[pair.Key.Trim()] = pair.Value ?? string.Empty;
        }
    }
}
