using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Services.GameSystems;

/// <summary>
/// Stable Retromind-owned system identifiers. Display metadata such as
/// MediaItem.Platform remains independent from these technical identifiers.
/// </summary>
public static class GameSystemCatalog
{
    public static IReadOnlyList<GameSystemDefinition> All { get; } =
    [
        new("arcade", "Arcade"),
        new("atari.2600", "Atari 2600"),
        new("atari.5200", "Atari 5200"),
        new("atari.7800", "Atari 7800"),
        new("atari.jaguar", "Atari Jaguar"),
        new("atari.lynx", "Atari Lynx"),
        new("bandai.wonderswan", "WonderSwan"),
        new("bandai.wonderswan-color", "WonderSwan Color"),
        new("coleco.colecovision", "ColecoVision"),
        new("commodore.64", "Commodore 64"),
        new("commodore.amiga", "Commodore Amiga"),
        new("mattel.intellivision", "Intellivision"),
        new("microsoft.ms-dos", "PC (MS-DOS)"),
        new("msx.msx", "MSX"),
        new("msx.msx2", "MSX2"),
        new("nec.pc-engine", "PC Engine / TurboGrafx-16"),
        new("nec.pc-engine-cd", "PC Engine CD / TurboGrafx-CD"),
        new("nintendo.game-boy", "Nintendo Game Boy"),
        new("nintendo.game-boy-advance", "Nintendo Game Boy Advance"),
        new("nintendo.game-boy-color", "Nintendo Game Boy Color"),
        new("nintendo.gamecube", "Nintendo GameCube"),
        new("nintendo.n64", "Nintendo 64"),
        new("nintendo.nds", "Nintendo DS"),
        new("nintendo.nes", "Nintendo Entertainment System"),
        new("nintendo.snes", "Super Nintendo Entertainment System"),
        new("nintendo.wii", "Nintendo Wii"),
        new("sega.32x", "Sega 32X"),
        new("sega.dreamcast", "Sega Dreamcast"),
        new("sega.game-gear", "Sega Game Gear"),
        new("sega.master-system", "Sega Master System"),
        new("sega.mega-drive", "Sega Mega Drive / Genesis"),
        new("sega.saturn", "Sega Saturn"),
        new("sega.sega-cd", "Sega CD / Mega-CD"),
        new("sega.sg-1000", "Sega SG-1000"),
        new("snk.neo-geo", "SNK Neo Geo"),
        new("snk.neo-geo-cd", "SNK Neo Geo CD"),
        new("snk.neo-geo-pocket", "SNK Neo Geo Pocket"),
        new("snk.neo-geo-pocket-color", "SNK Neo Geo Pocket Color"),
        new("sony.playstation", "Sony PlayStation"),
        new("sony.playstation-2", "Sony PlayStation 2"),
        new("sony.psp", "Sony PlayStation Portable")
    ];

    public static GameSystemDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return All.FirstOrDefault(system =>
            string.Equals(system.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Find(id)?.Id ?? id.Trim();
    }
}
