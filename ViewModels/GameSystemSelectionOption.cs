using System;
using System.Collections.Generic;
using Retromind.Services.GameSystems;

namespace Retromind.ViewModels;

public sealed record GameSystemSelectionOption(string? Id, string DisplayName)
{
    public static IReadOnlyList<GameSystemSelectionOption> Create(
        string inheritDisplayName,
        string? selectedSystemId)
    {
        var options = new List<GameSystemSelectionOption>
        {
            new(null, inheritDisplayName)
        };

        foreach (var system in GameSystemCatalog.All)
            options.Add(new GameSystemSelectionOption(system.Id, system.DisplayName));

        var normalizedSelectedId = GameSystemCatalog.NormalizeId(selectedSystemId);
        if (normalizedSelectedId != null && GameSystemCatalog.Find(normalizedSelectedId) == null)
        {
            options.Add(new GameSystemSelectionOption(
                normalizedSelectedId,
                normalizedSelectedId));
        }

        return options;
    }

    public static GameSystemSelectionOption? Find(
        IEnumerable<GameSystemSelectionOption> options,
        string? systemId)
    {
        var normalizedSystemId = GameSystemCatalog.NormalizeId(systemId);
        foreach (var option in options)
        {
            if (string.Equals(option.Id, normalizedSystemId, StringComparison.OrdinalIgnoreCase))
                return option;
        }

        return null;
    }
}
