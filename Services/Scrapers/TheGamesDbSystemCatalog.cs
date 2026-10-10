using System;
using System.Collections.Generic;
using Retromind.Services.GameSystems;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Maps Retromind-owned game-system identifiers to TheGamesDB platform IDs.
/// Some Retromind systems correspond to multiple provider platforms.
/// </summary>
internal static class TheGamesDbSystemCatalog
{
    private static readonly IReadOnlyDictionary<string, int[]> PlatformIds =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["arcade"] = [23],
            ["atari.2600"] = [22],
            ["atari.5200"] = [26],
            ["atari.7800"] = [27],
            ["atari.jaguar"] = [28],
            ["atari.lynx"] = [4924],
            ["bandai.wonderswan"] = [4925],
            ["bandai.wonderswan-color"] = [4926],
            ["coleco.colecovision"] = [31],
            ["commodore.64"] = [40],
            ["commodore.amiga"] = [4911],
            ["mattel.intellivision"] = [32],
            ["microsoft.pc"] = [1],
            ["msx.msx"] = [4929],
            ["msx.msx2"] = [4929],
            ["nec.pc-engine"] = [34],
            ["nec.pc-engine-cd"] = [4955],
            ["nintendo.game-boy"] = [4],
            ["nintendo.game-boy-advance"] = [5],
            ["nintendo.game-boy-color"] = [41],
            ["nintendo.gamecube"] = [2],
            ["nintendo.n64"] = [3],
            ["nintendo.nds"] = [8],
            ["nintendo.nes"] = [7],
            ["nintendo.snes"] = [6],
            ["nintendo.wii"] = [9],
            ["sega.32x"] = [33],
            ["sega.dreamcast"] = [16],
            ["sega.game-gear"] = [20],
            ["sega.master-system"] = [35],
            ["sega.mega-drive"] = [18, 36],
            ["sega.saturn"] = [17],
            ["sega.sega-cd"] = [21],
            ["sega.sg-1000"] = [4949],
            ["snk.neo-geo"] = [24],
            ["snk.neo-geo-cd"] = [4956],
            ["snk.neo-geo-pocket"] = [4922],
            ["snk.neo-geo-pocket-color"] = [4923],
            ["sony.playstation"] = [10],
            ["sony.playstation-2"] = [11],
            ["sony.psp"] = [13]
        };

    public static bool TryGetPlatformIds(
        string? gameSystemId,
        out IReadOnlyList<int> theGamesDbPlatformIds)
    {
        var normalized = GameSystemCatalog.NormalizeId(gameSystemId);
        if (normalized != null &&
            PlatformIds.TryGetValue(normalized, out var platformIds))
        {
            theGamesDbPlatformIds = platformIds;
            return true;
        }

        theGamesDbPlatformIds = Array.Empty<int>();
        return false;
    }
}