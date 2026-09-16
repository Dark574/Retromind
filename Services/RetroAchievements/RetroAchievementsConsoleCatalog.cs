using System;
using System.Collections.Generic;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Maps Retromind-owned game-system identifiers to the console identifiers
/// defined by the pinned rcheevos version.
/// </summary>
public static class RetroAchievementsConsoleCatalog
{
    private static readonly IReadOnlyDictionary<string, uint> ConsoleIds =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["arcade"] = 27,
            ["atari.2600"] = 25,
            ["atari.7800"] = 51,
            ["atari.jaguar"] = 17,
            ["atari.lynx"] = 13,
            ["bandai.wonderswan"] = 53,
            ["bandai.wonderswan-color"] = 53,
            ["coleco.colecovision"] = 44,
            ["commodore.64"] = 30,
            ["mattel.intellivision"] = 45,
            ["microsoft.ms-dos"] = 26,
            ["msx.msx"] = 29,
            ["msx.msx2"] = 29,
            ["nec.pc-engine"] = 8,
            ["nec.pc-engine-cd"] = 76,
            ["nintendo.game-boy"] = 4,
            ["nintendo.game-boy-advance"] = 5,
            ["nintendo.game-boy-color"] = 6,
            ["nintendo.gamecube"] = 16,
            ["nintendo.n64"] = 2,
            ["nintendo.nds"] = 18,
            ["nintendo.nes"] = 7,
            ["nintendo.snes"] = 3,
            ["nintendo.wii"] = 19,
            ["sega.32x"] = 10,
            ["sega.dreamcast"] = 40,
            ["sega.game-gear"] = 15,
            ["sega.master-system"] = 11,
            ["sega.mega-drive"] = 1,
            ["sega.saturn"] = 39,
            ["sega.sega-cd"] = 9,
            ["sega.sg-1000"] = 33,
            ["snk.neo-geo"] = 27,
            ["snk.neo-geo-cd"] = 56,
            ["snk.neo-geo-pocket"] = 14,
            ["snk.neo-geo-pocket-color"] = 14,
            ["sony.playstation"] = 12,
            ["sony.playstation-2"] = 21,
            ["sony.psp"] = 41
        };

    public static bool TryGetConsoleId(string? gameSystemId, out uint consoleId)
    {
        if (string.IsNullOrWhiteSpace(gameSystemId))
        {
            consoleId = 0;
            return false;
        }

        return ConsoleIds.TryGetValue(gameSystemId.Trim(), out consoleId);
    }
}
