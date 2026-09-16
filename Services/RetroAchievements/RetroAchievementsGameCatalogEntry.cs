using System.Collections.Generic;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsGameCatalogEntry
{
    public int GameId { get; init; }
    public string Title { get; init; } = string.Empty;
    public uint ConsoleId { get; init; }
    public string ConsoleName { get; init; } = string.Empty;
    public string? ImageIconPath { get; init; }
    public int AchievementCount { get; init; }
    public List<string> Hashes { get; init; } = new();
}
