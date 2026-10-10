using System;
using System.Collections.Generic;
using Retromind.Services.GameSystems;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Maps Retromind-owned game-system identifiers to IGDB platform IDs.
/// Provider-specific IDs deliberately stay outside the provider-neutral model.
/// </summary>
internal static class IgdbSystemCatalog
{
    private static readonly IReadOnlyDictionary<string, int[]> PlatformIds =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["arcade"] = [52],
            ["atari.2600"] = [59],
            ["atari.5200"] = [66],
            ["atari.7800"] = [60],
            ["atari.jaguar"] = [62],
            ["atari.lynx"] = [61],
            ["bandai.wonderswan"] = [57],
            ["bandai.wonderswan-color"] = [123],
            ["coleco.colecovision"] = [68],
            ["commodore.64"] = [15],
            ["commodore.amiga"] = [16],
            ["mattel.intellivision"] = [67],
            ["microsoft.pc"] = [6, 13],
            ["microsoft.ms-dos"] = [13],
            ["microsoft.windows"] = [6],
            ["msx.msx"] = [27],
            ["msx.msx2"] = [53],
            ["nec.pc-engine"] = [86],
            ["nec.pc-engine-cd"] = [150],
            ["nintendo.game-boy"] = [33],
            ["nintendo.game-boy-advance"] = [24],
            ["nintendo.game-boy-color"] = [22],
            ["nintendo.gamecube"] = [21],
            ["nintendo.n64"] = [4],
            ["nintendo.nds"] = [20],
            ["nintendo.nes"] = [18],
            ["nintendo.snes"] = [19],
            ["nintendo.wii"] = [5],
            ["sega.32x"] = [30],
            ["sega.dreamcast"] = [23],
            ["sega.game-gear"] = [35],
            ["sega.master-system"] = [64],
            ["sega.mega-drive"] = [29],
            ["sega.saturn"] = [32],
            ["sega.sega-cd"] = [78],
            ["sega.sg-1000"] = [84],
            ["snk.neo-geo"] = [80],
            ["snk.neo-geo-cd"] = [136],
            ["snk.neo-geo-pocket"] = [119],
            ["snk.neo-geo-pocket-color"] = [120],
            ["sony.playstation"] = [7],
            ["sony.playstation-2"] = [8],
            ["sony.psp"] = [38]
        };

    public static bool TryGetPlatformIds(
        string? gameSystemId,
        out IReadOnlyList<int> igdbPlatformIds)
    {
        var normalized = GameSystemCatalog.NormalizeId(gameSystemId);
        if (normalized != null &&
            PlatformIds.TryGetValue(normalized, out var platformIds))
        {
            igdbPlatformIds = platformIds;
            return true;
        }

        igdbPlatformIds = Array.Empty<int>();
        return false;
    }
}