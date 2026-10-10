using System;
using System.Collections.Generic;
using Retromind.Services.GameSystems;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Maps Retromind-owned game-system identifiers to the stable numeric IDs
/// published by ScreenScraper. Provider IDs deliberately stay outside the
/// provider-neutral game-system model.
/// </summary>
internal static class ScreenScraperSystemCatalog
{
    private static readonly IReadOnlyDictionary<string, int[]> SystemIds =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["arcade"] = [75],
            ["atari.2600"] = [26],
            ["atari.5200"] = [40],
            ["atari.7800"] = [41],
            ["atari.jaguar"] = [27],
            ["atari.lynx"] = [28],
            ["bandai.wonderswan"] = [45],
            ["bandai.wonderswan-color"] = [46],
            ["coleco.colecovision"] = [48],
            ["commodore.64"] = [66],
            ["commodore.amiga"] = [64],
            ["mattel.intellivision"] = [115],
            ["microsoft.pc"] = [135, 138],
            ["microsoft.ms-dos"] = [135],
            ["microsoft.windows"] = [138],
            ["msx.msx"] = [113],
            ["msx.msx2"] = [116],
            ["nec.pc-engine"] = [31],
            ["nec.pc-engine-cd"] = [114],
            ["nintendo.game-boy"] = [9],
            ["nintendo.game-boy-advance"] = [12],
            ["nintendo.game-boy-color"] = [10],
            ["nintendo.gamecube"] = [13],
            ["nintendo.n64"] = [14],
            ["nintendo.nds"] = [15],
            ["nintendo.nes"] = [3],
            ["nintendo.snes"] = [4],
            ["nintendo.wii"] = [16],
            ["sega.32x"] = [19],
            ["sega.dreamcast"] = [23],
            ["sega.game-gear"] = [21],
            ["sega.master-system"] = [2],
            ["sega.mega-drive"] = [1],
            ["sega.saturn"] = [22],
            ["sega.sega-cd"] = [20],
            ["sega.sg-1000"] = [109],
            ["snk.neo-geo"] = [142],
            ["snk.neo-geo-cd"] = [70],
            ["snk.neo-geo-pocket"] = [25],
            ["snk.neo-geo-pocket-color"] = [82],
            ["sony.playstation"] = [57],
            ["sony.playstation-2"] = [58],
            ["sony.psp"] = [61]
        };

    public static bool TryGetSystemIds(string? gameSystemId, out IReadOnlyList<int> screenScraperSystemIds)
    {
        var normalized = GameSystemCatalog.NormalizeId(gameSystemId);
        if (normalized != null && SystemIds.TryGetValue(normalized, out var systemIds))
        {
            screenScraperSystemIds = systemIds;
            return true;
        }

        screenScraperSystemIds = Array.Empty<int>();
        return false;
    }
}
